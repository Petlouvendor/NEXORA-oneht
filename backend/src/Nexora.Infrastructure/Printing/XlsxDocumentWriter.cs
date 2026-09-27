using System.Globalization;
using ClosedXML.Excel;
using Nexora.Application.Printing;
using Nexora.Contracts.Printing;

namespace Nexora.Infrastructure.Printing;

/// <summary>
/// Vrai classeur Excel (.xlsx, ClosedXML, MIT). Les cellules gardent leur type : montants et quantités
/// en nombres (formats #,##0.00), dates en dates, pourcentages en %. En-tête de l'entreprise, ligne
/// d'en-tête figée, filtre automatique, ligne de totaux, mise en page d'impression (ajustée à la largeur).
/// Un seul tableau → une feuille ; plusieurs → une feuille par tableau (+ « Synthèse » si le document a
/// des chiffres clés).
/// </summary>
internal static class XlsxDocumentWriter
{
    private static readonly XLColor HeadFill = XLColor.FromHtml("#E6F2EF");
    private static readonly XLColor TotalFill = XLColor.FromHtml("#F3F4F6");
    private static readonly XLColor Accent = XLColor.FromHtml("#0E7C6B");
    private static readonly XLColor Muted = XLColor.FromHtml("#6B7280");
    private static readonly XLColor StampRed = XLColor.FromHtml("#B91C1C");

    public static byte[] Write(PrintDocument d, PrintIssuer issuer, PrintMeta meta)
    {
        var tz = PrintValues.Zone(issuer.TimeZone);
        using var wb = new XLWorkbook();
        wb.Properties.Author = issuer.Name;
        wb.Properties.Title = d.Reference is null ? d.Title : $"{d.Title} {d.Reference}";
        wb.Properties.Company = issuer.LegalName ?? issuer.Name;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tables = d.Tables ?? [];
        var hasBlocks = (d.Parties?.Count ?? 0) + (d.Details?.Count ?? 0) + (d.Summary?.Count ?? 0) + (d.Notes?.Count ?? 0) > 0
                        || d.Stamp is not null;

        if (tables.Count <= 1)
        {
            var sheet = new Sheet(wb.Worksheets.Add(SheetName(d.Title, names)));
            Heading(sheet, d, issuer, null);
            Blocks(sheet, d, tz);
            if (tables.Count == 1) Table(sheet, tables[0], tz, primary: true, withTitle: true);
            Footer(sheet, d, meta, tz);
            sheet.Finish(tables.FirstOrDefault()?.Columns.Count ?? 2);
        }
        else
        {
            if (hasBlocks)
            {
                var summary = new Sheet(wb.Worksheets.Add(SheetName("Synthèse", names)));
                Heading(summary, d, issuer, null);
                Blocks(summary, d, tz);
                Footer(summary, d, meta, tz);
                summary.Finish(2);
            }
            for (var i = 0; i < tables.Count; i++)
            {
                var t = tables[i];
                var sheet = new Sheet(wb.Worksheets.Add(SheetName(t.Title ?? $"Tableau {i + 1}", names)));
                Heading(sheet, d, issuer, t.Title);
                Table(sheet, t, tz, primary: true, withTitle: false);
                Footer(sheet, d, meta, tz);
                sheet.Finish(t.Columns.Count);
            }
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Feuille en cours d'écriture : ligne courante et largeur utile de chaque colonne.</summary>
    private sealed class Sheet(IXLWorksheet ws)
    {
        public IXLWorksheet Ws { get; } = ws;
        public int Row { get; set; } = 1;
        private readonly Dictionary<int, int> _widths = [];

        public void Measure(int col, string? text)
        {
            var len = string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Max(l => l.Length);
            _widths[col] = Math.Max(_widths.GetValueOrDefault(col), len);
        }

        public void Finish(int columns)
        {
            foreach (var (col, len) in _widths) Ws.Column(col).Width = Math.Clamp(len + 2, 9, 60);
            Ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
            Ws.PageSetup.PageOrientation = columns > 7 ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
            Ws.PageSetup.FitToPages(1, 0);
            Ws.PageSetup.Margins.Left = 0.5;
            Ws.PageSetup.Margins.Right = 0.5;
        }
    }

    private static void Heading(Sheet s, PrintDocument d, PrintIssuer issuer, string? tableTitle)
    {
        var ws = s.Ws;
        var c = ws.Cell(s.Row++, 1);
        c.Value = issuer.Name;
        c.Style.Font.Bold = true;
        c.Style.Font.FontSize = 13;
        c.Style.Font.FontColor = Accent;

        var legal = new[]
        {
            issuer.LegalName is { } ln && !string.Equals(ln, issuer.Name, StringComparison.OrdinalIgnoreCase) ? ln : null,
            issuer.TaxId is { } tax ? $"NIF : {tax}" : null,
            issuer.Address,
            issuer.Phone is { } ph ? $"Tél. : {ph}" : null,
        }.Where(x => !string.IsNullOrWhiteSpace(x));
        var legalLine = string.Join(" · ", legal);
        if (legalLine.Length > 0)
        {
            var l = ws.Cell(s.Row++, 1);
            l.Value = legalLine;
            l.Style.Font.FontColor = Muted;
            l.Style.Font.FontSize = 9;
        }

        s.Row++;
        var title = ws.Cell(s.Row++, 1);
        title.Value = d.Reference is null ? d.Title : $"{d.Title} — {d.Reference}";
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 14;
        if (!string.IsNullOrWhiteSpace(d.Subtitle))
        {
            var sub = ws.Cell(s.Row++, 1);
            sub.Value = d.Subtitle;
            sub.Style.Font.FontColor = Muted;
        }
        if (!string.IsNullOrWhiteSpace(tableTitle) && tableTitle != d.Title)
        {
            var tt = ws.Cell(s.Row++, 1);
            tt.Value = tableTitle;
            tt.Style.Font.Bold = true;
            tt.Style.Font.FontSize = 12;
        }
        if (!string.IsNullOrWhiteSpace(d.Stamp))
        {
            var st = ws.Cell(s.Row++, 1);
            st.Value = d.Stamp;
            st.Style.Font.Bold = true;
            st.Style.Font.FontSize = 12;
            st.Style.Font.FontColor = StampRed;
        }
        s.Row++;
    }

    private static void Blocks(Sheet s, PrintDocument d, TimeZoneInfo tz)
    {
        var ws = s.Ws;
        foreach (var party in d.Parties ?? [])
        {
            var label = ws.Cell(s.Row, 1);
            label.Value = party.Label;
            label.Style.Font.Bold = true;
            label.Style.Font.FontColor = Muted;
            s.Measure(1, party.Label);
            var value = ws.Cell(s.Row++, 2);
            value.Value = string.Join(" · ", party.Lines);
            value.Style.Font.Bold = true;
        }
        foreach (var f in (d.Details ?? []).Concat(d.Summary ?? []))
        {
            var label = ws.Cell(s.Row, 1);
            label.Value = f.Label;
            label.Style.Font.Bold = f.Emphasis;
            s.Measure(1, f.Label);
            var cell = ws.Cell(s.Row++, 2);
            SetValue(cell, f.Value, f.Kind, f.Currency, tz, withCurrency: true);
            cell.Style.Font.Bold = f.Emphasis;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            s.Measure(2, PrintValues.Text(f.Value, f.Kind, f.Currency, tz, withCurrency: true));
            if (f.Emphasis)
            {
                ws.Range(label, cell).Style.Fill.BackgroundColor = HeadFill;
                ws.Range(label, cell).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            }
        }
        foreach (var note in d.Notes ?? [])
        {
            var n = ws.Cell(s.Row++, 1);
            n.Value = note;
            n.Style.Font.Italic = true;
            n.Style.Font.FontColor = Muted;
        }
        if ((d.Parties?.Count ?? 0) + (d.Details?.Count ?? 0) + (d.Summary?.Count ?? 0) + (d.Notes?.Count ?? 0) > 0) s.Row++;
    }

    private static void Table(Sheet s, PrintTable t, TimeZoneInfo tz, bool primary, bool withTitle)
    {
        var ws = s.Ws;
        var cols = t.Columns.Count;
        if (cols == 0) return;
        if (withTitle && !string.IsNullOrWhiteSpace(t.Title))
        {
            var title = ws.Cell(s.Row++, 1);
            title.Value = t.Title;
            title.Style.Font.Bold = true;
            title.Style.Font.FontSize = 12;
        }
        var headerRow = s.Row;
        for (var i = 0; i < cols; i++)
        {
            var header = PrintValues.Header(t.Columns[i]);
            var h = ws.Cell(headerRow, i + 1);
            h.Value = header;
            s.Measure(i + 1, header);
        }
        var head = ws.Range(headerRow, 1, headerRow, cols);
        head.Style.Font.Bold = true;
        head.Style.Fill.BackgroundColor = HeadFill;
        head.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        head.Style.Alignment.WrapText = true;
        s.Row++;

        foreach (var row in t.Rows)
        {
            for (var i = 0; i < cols; i++)
            {
                var col = t.Columns[i];
                var value = i < row.Count ? row[i] : null;
                SetValue(ws.Cell(s.Row, i + 1), value, col.Kind, col.Currency, tz);
                s.Measure(i + 1, PrintValues.Text(value, col.Kind, col.Currency, tz));
            }
            s.Row++;
        }
        var lastData = s.Row - 1;

        if (t.Totals is { } totals)
        {
            for (var i = 0; i < cols && i < totals.Count; i++)
                SetValue(ws.Cell(s.Row, i + 1), totals[i], t.Columns[i].Kind, t.Columns[i].Currency, tz);
            var tr = ws.Range(s.Row, 1, s.Row, cols);
            tr.Style.Font.Bold = true;
            tr.Style.Fill.BackgroundColor = TotalFill;
            tr.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            s.Row++;
        }

        if (primary)
        {
            ws.SheetView.FreezeRows(headerRow);
            ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
            if (lastData > headerRow) ws.Range(headerRow, 1, lastData, cols).SetAutoFilter();
        }
        s.Row++;
    }

    private static void Footer(Sheet s, PrintDocument d, PrintMeta meta, TimeZoneInfo tz)
    {
        var at = TimeZoneInfo.ConvertTime(meta.PrintedAt, tz).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var c = s.Ws.Cell(s.Row++, 1);
        c.Value = string.IsNullOrWhiteSpace(meta.PrintedBy) ? $"Exporté le {at}" : $"Exporté le {at} par {meta.PrintedBy}";
        c.Style.Font.FontColor = Muted;
        c.Style.Font.FontSize = 8;
        if (!d.Official)
        {
            var n = s.Ws.Cell(s.Row++, 1);
            n.Value = "Export de données affichées à l'écran — document non contractuel.";
            n.Style.Font.FontColor = Muted;
            n.Style.Font.FontSize = 8;
        }
    }

    /// <summary>Écrit une valeur typée : nombre, date ou texte (jamais une formule).</summary>
    private static void SetValue(IXLCell cell, object? value, string kind, string? currency, TimeZoneInfo tz, bool withCurrency = false)
    {
        var v = PrintValues.Unwrap(value);
        if (v is null) return;
        var suffix = withCurrency && !string.IsNullOrEmpty(currency) ? $" \"{currency}\"" : "";
        switch (kind)
        {
            case PrintKinds.Money when PrintValues.AsDecimal(v) is { } m:
                cell.Value = (double)m;
                cell.Style.NumberFormat.Format = "#,##0.00" + suffix;
                return;
            case PrintKinds.Number when PrintValues.AsDecimal(v) is { } n:
                cell.Value = (double)n;
                cell.Style.NumberFormat.Format = n == Math.Round(n) ? "#,##0" : "#,##0.00#";
                return;
            case PrintKinds.Integer when PrintValues.AsDecimal(v) is { } i:
                cell.Value = (double)Math.Round(i);
                cell.Style.NumberFormat.Format = "#,##0";
                return;
            case PrintKinds.Percent when PrintValues.AsDecimal(v) is { } p:
                cell.Value = (double)(p / 100m);
                cell.Style.NumberFormat.Format = "0.0%";
                return;
            case PrintKinds.Date when PrintValues.AsDate(v, tz) is { } d:
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "dd/mm/yyyy";
                return;
            case PrintKinds.DateTime when PrintValues.AsLocalDateTime(v, tz) is { } dt:
                cell.Value = dt;
                cell.Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
                return;
        }
        cell.Value = v switch
        {
            decimal dm => (double)dm,
            double db => db,
            int n => n,
            long l => l,
            bool b => b ? "Oui" : "Non",
            _ => PrintValues.Text(v, PrintKinds.Text, null, tz),
        };
    }

    /// <summary>Nom de feuille Excel valide (31 caractères, sans []:*?/\) et unique.</summary>
    private static string SheetName(string raw, HashSet<string> used)
    {
        var clean = new string(raw.Select(ch => "[]:*?/\\".Contains(ch) ? ' ' : ch).ToArray()).Trim();
        if (clean.Length == 0) clean = "Feuille";
        if (clean.Length > 31) clean = clean[..31].TrimEnd();
        var name = clean;
        for (var i = 2; !used.Add(name); i++)
        {
            var tag = $" ({i})";
            name = (clean.Length + tag.Length > 31 ? clean[..(31 - tag.Length)] : clean) + tag;
        }
        return name;
    }
}
