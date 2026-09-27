using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Nexora.Application.Printing;
using Nexora.Contracts.Printing;

namespace Nexora.Infrastructure.Printing;

/// <summary>
/// Vrai PDF (MigraDoc / PDFsharp, MIT) : A4 portrait ou paysage selon la largeur des tableaux, ou
/// ticket 80 mm pour imprimante thermique. En-tête de l'entreprise, blocs client / fournisseur,
/// tableaux avec ligne d'en-tête répétée à chaque page, chiffres clés encadrés, signatures, pied de
/// page « Imprimé le … par … · Page n / N ».
/// </summary>
internal static class PdfDocumentWriter
{
    private static readonly Color Ink = Hex("#1F2937");
    private static readonly Color Muted = Hex("#6B7280");
    private static readonly Color Rule = Hex("#D1D5DB");
    private static readonly Color HeadFill = Hex("#EEF2F6");
    private static readonly Color TotalFill = Hex("#F3F4F6");
    private static readonly Color StampRed = Hex("#B91C1C");
    private const string DefaultAccent = "#0E7C6B";   // turquoise du design system, assombri pour l'impression

    public static byte[] Write(PrintDocument d, PrintIssuer issuer, PrintMeta meta, bool ticket)
    {
        InterFontResolver.EnsureInstalled();
        if (!ticket) return Render(Build(d, issuer, meta, ticket: false, 0));

        // Ticket : 1re passe sur un rouleau « infini », mesure du bas du contenu, puis page à la hauteur
        // exacte — l'imprimante thermique ne débite pas de papier blanc.
        var probe = new PdfDocumentRenderer { Document = Build(d, issuer, meta, ticket: true, 3000) };
        probe.PrepareRenderPages();
        var height = 3000.0;
        if (probe.PageCount == 1 && probe.DocumentRenderer.GetRenderInfoFromPage(1) is { Length: > 0 } infos)
        {
            var bottom = infos.Max(i => i.LayoutInfo.ContentArea.Y.Point + i.LayoutInfo.ContentArea.Height.Point);
            height = Math.Max(60, bottom * 25.4 / 72 + 8);
        }
        return Render(Build(d, issuer, meta, ticket: true, height));
    }

    private static byte[] Render(Document doc)
    {
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static Document Build(PrintDocument d, PrintIssuer issuer, PrintMeta meta, bool ticket, double ticketHeightMm)
    {
        var tz = PrintValues.Zone(issuer.TimeZone);
        var accent = PrintAccent(Hex(IsHex(issuer.AccentColor) ? issuer.AccentColor! : DefaultAccent));

        var doc = new Document();
        doc.Info.Title = d.Reference is null ? d.Title : $"{d.Title} {d.Reference}";
        doc.Info.Author = issuer.Name;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = InterFontResolver.Family;
        normal.Font.Size = ticket ? 7.5 : 9;
        normal.Font.Color = Ink;

        var section = doc.AddSection();
        var ps = section.PageSetup;
        if (ticket)
        {
            ps.PageWidth = Unit.FromMillimeter(80);
            ps.PageHeight = Unit.FromMillimeter(ticketHeightMm);
            ps.LeftMargin = Unit.FromMillimeter(4);
            ps.RightMargin = Unit.FromMillimeter(4);
            ps.TopMargin = Unit.FromMillimeter(4);
            ps.BottomMargin = Unit.FromMillimeter(4);
            Ticket(section, d, issuer, meta, tz, accent);
        }
        else
        {
            var landscape = d.Landscape || (d.Tables?.Any(t => t.Columns.Count > 7) ?? false);
            ps.PageFormat = PageFormat.A4;
            ps.Orientation = landscape ? Orientation.Landscape : Orientation.Portrait;
            ps.TopMargin = Unit.FromCentimeter(1.4);
            ps.BottomMargin = Unit.FromCentimeter(2);
            ps.LeftMargin = Unit.FromCentimeter(1.5);
            ps.RightMargin = Unit.FromCentimeter(1.5);
            ps.FooterDistance = Unit.FromCentimeter(0.8);
            A4(section, d, issuer, meta, tz, accent, (landscape ? 29.7 : 21.0) - 3.0);
        }
        return doc;
    }

    // ------------------------------------------------------------------------------ A4

    private static void A4(Section s, PrintDocument d, PrintIssuer iss, PrintMeta meta, TimeZoneInfo tz, Color accent, double w)
    {
        // En-tête : émetteur à gauche, document à droite.
        var head = s.AddTable();
        head.Borders.Visible = false;
        head.AddColumn(Unit.FromCentimeter(w * 0.55));
        head.AddColumn(Unit.FromCentimeter(w * 0.45));
        var hr = head.AddRow();
        var left = hr.Cells[0];
        Para(left.AddParagraph(), iss.Name, 13, bold: true, color: accent);
        foreach (var line in IssuerLines(iss)) Para(left.AddParagraph(), line, 7.5, color: Muted);
        var right = hr.Cells[1];
        right.Format.Alignment = ParagraphAlignment.Right;
        Para(right.AddParagraph(), d.Title, 14, bold: true);
        if (!string.IsNullOrWhiteSpace(d.Reference)) Para(right.AddParagraph(), d.Reference!, 10, bold: true, color: accent);
        if (!string.IsNullOrWhiteSpace(d.Subtitle)) Para(right.AddParagraph(), d.Subtitle!, 8, color: Muted);

        var rule = s.AddParagraph();
        rule.Format.Borders.Bottom.Width = 1;
        rule.Format.Borders.Bottom.Color = accent;
        rule.Format.SpaceAfter = Unit.FromPoint(8);

        Stamp(s, d.Stamp, 13);

        if (d.Parties is { Count: > 0 } parties)
        {
            var n = Math.Min(parties.Count, 3);
            var t = s.AddTable();
            t.Borders.Visible = false;
            for (var i = 0; i < n; i++) t.AddColumn(Unit.FromCentimeter(w / n));
            for (var i = 0; i < parties.Count; i += n)
            {
                var r = t.AddRow();
                for (var j = 0; j < n && i + j < parties.Count; j++)
                {
                    var cell = r.Cells[j];
                    var party = parties[i + j];
                    Para(cell.AddParagraph(), party.Label.ToUpperInvariant(), 7, bold: true, color: Muted);
                    for (var k = 0; k < party.Lines.Count; k++)
                        Para(cell.AddParagraph(), party.Lines[k], k == 0 ? 10 : 8.5, bold: k == 0);
                }
            }
            Spacer(s, 8);
        }

        if (d.Details is { Count: > 0 } details)
        {
            var t = s.AddTable();
            t.Borders.Visible = false;
            t.TopPadding = Unit.FromPoint(1.5);
            t.BottomPadding = Unit.FromPoint(1.5);
            foreach (var share in new[] { 0.17, 0.33, 0.17, 0.33 }) t.AddColumn(Unit.FromCentimeter(w * share));
            for (var i = 0; i < details.Count; i += 2)
            {
                var r = t.AddRow();
                for (var j = 0; j < 2 && i + j < details.Count; j++)
                {
                    var f = details[i + j];
                    Para(r.Cells[j * 2].AddParagraph(), f.Label, 8, color: Muted);
                    Para(r.Cells[j * 2 + 1].AddParagraph(), PrintValues.Text(f.Value, f.Kind, f.Currency, tz, withCurrency: true), 8.5, bold: f.Emphasis);
                }
            }
            Spacer(s, 8);
        }

        foreach (var table in d.Tables ?? []) GridTable(s, table, tz, w);

        if (d.Summary is { Count: > 0 } summary)
        {
            var t = s.AddTable();
            t.Rows.Alignment = RowAlignment.Right;
            t.Borders.Visible = false;
            t.TopPadding = Unit.FromPoint(3);
            t.BottomPadding = Unit.FromPoint(3);
            t.LeftPadding = Unit.FromPoint(5);
            t.RightPadding = Unit.FromPoint(5);
            t.AddColumn(Unit.FromCentimeter(Math.Min(6.5, w * 0.4)));
            t.AddColumn(Unit.FromCentimeter(4.5)).Format.Alignment = ParagraphAlignment.Right;
            foreach (var f in summary)
            {
                var r = t.AddRow();
                Para(r.Cells[0].AddParagraph(), f.Label, f.Emphasis ? 10 : 8.5, bold: f.Emphasis);
                Para(r.Cells[1].AddParagraph(), PrintValues.Text(f.Value, f.Kind, f.Currency, tz, withCurrency: true), f.Emphasis ? 11 : 9, bold: true);
                if (f.Emphasis)
                {
                    r.Shading.Color = Tint(accent, 0.86);
                    r.Borders.Top.Width = 1;
                    r.Borders.Top.Color = accent;
                }
                else
                {
                    r.Borders.Bottom.Width = 0.4;
                    r.Borders.Bottom.Color = Rule;
                }
            }
            Spacer(s, 8);
        }

        foreach (var note in d.Notes ?? []) Para(s.AddParagraph(), note, 8, color: Muted).Format.SpaceAfter = Unit.FromPoint(3);

        if (d.Signatures is { Count: > 0 } signatures)
        {
            Spacer(s, 14);
            var t = s.AddTable();
            t.Borders.Visible = false;
            t.LeftPadding = Unit.FromPoint(12);
            t.RightPadding = Unit.FromPoint(12);
            foreach (var _ in signatures) t.AddColumn(Unit.FromCentimeter(w / signatures.Count));
            var r = t.AddRow();
            for (var i = 0; i < signatures.Count; i++)
            {
                var p = Para(r.Cells[i].AddParagraph(), signatures[i], 8, color: Muted);
                p.Format.Alignment = ParagraphAlignment.Center;
                p.Format.SpaceBefore = Unit.FromPoint(34);
                p.Format.Borders.Top.Width = 0.6;
                p.Format.Borders.Top.Color = Ink;
            }
        }

        var footer = s.Footers.Primary;
        if (!string.IsNullOrWhiteSpace(iss.Footer)) Center(Para(footer.AddParagraph(), iss.Footer!, 7, color: Muted));
        var fp = Center(Para(footer.AddParagraph(), PrintedLine(meta, tz) + " · Page ", 7, color: Muted));
        fp.AddPageField();
        fp.AddText(" / ");
        fp.AddNumPagesField();
        if (!d.Official) Center(Para(footer.AddParagraph(), NotContractual, 7, bold: true, color: Muted));
    }

    private static void GridTable(Section s, PrintTable pt, TimeZoneInfo tz, double w)
    {
        if (!string.IsNullOrWhiteSpace(pt.Title))
        {
            var p = Para(s.AddParagraph(), pt.Title!, 10, bold: true);
            p.Format.SpaceBefore = Unit.FromPoint(4);
            p.Format.SpaceAfter = Unit.FromPoint(3);
            p.Format.KeepWithNext = true;
        }
        var cols = pt.Columns.Count;
        if (cols == 0) return;

        var weights = pt.Columns.Select(Weight).ToArray();
        var total = weights.Sum();
        var t = s.AddTable();
        t.Borders.Width = 0.4;
        t.Borders.Color = Rule;
        t.Format.Font.Size = cols > 9 ? 6.8 : cols > 6 ? 7.5 : 8.3;
        t.TopPadding = Unit.FromPoint(2);
        t.BottomPadding = Unit.FromPoint(2);
        t.LeftPadding = Unit.FromPoint(3);
        t.RightPadding = Unit.FromPoint(3);
        for (var i = 0; i < cols; i++)
            t.AddColumn(Unit.FromCentimeter(w * weights[i] / total)).Format.Alignment =
                PrintValues.IsNumeric(pt.Columns[i].Kind) ? ParagraphAlignment.Right : ParagraphAlignment.Left;

        var h = t.AddRow();
        h.HeadingFormat = true;
        h.Shading.Color = HeadFill;
        h.Format.Font.Bold = true;
        for (var i = 0; i < cols; i++) h.Cells[i].AddParagraph(PrintValues.Header(pt.Columns[i]));

        if (pt.Rows.Count == 0)
        {
            var r = t.AddRow();
            r.Cells[0].MergeRight = cols - 1;
            Para(r.Cells[0].AddParagraph(), "Aucune ligne.", 8, color: Muted);
        }
        foreach (var row in pt.Rows)
        {
            var r = t.AddRow();
            for (var i = 0; i < cols; i++)
                r.Cells[i].AddParagraph(PrintValues.Text(i < row.Count ? row[i] : null, pt.Columns[i].Kind, pt.Columns[i].Currency, tz));
        }
        if (pt.Totals is { } totals)
        {
            var r = t.AddRow();
            r.Shading.Color = TotalFill;
            r.Format.Font.Bold = true;
            for (var i = 0; i < cols; i++)
                r.Cells[i].AddParagraph(PrintValues.Text(i < totals.Count ? totals[i] : null, pt.Columns[i].Kind, pt.Columns[i].Currency, tz));
        }
        Spacer(s, 8);
    }

    // ------------------------------------------------------------------------------ Ticket 80 mm

    private const double TicketWidthCm = 7.2;

    private static void Ticket(Section s, PrintDocument d, PrintIssuer iss, PrintMeta meta, TimeZoneInfo tz, Color accent)
    {
        Center(Para(s.AddParagraph(), iss.Name, 10, bold: true));
        foreach (var line in IssuerLines(iss)) Center(Para(s.AddParagraph(), line, 6.5, color: Muted));
        Dotted(s);
        Center(Para(s.AddParagraph(), d.Title, 9, bold: true));
        if (!string.IsNullOrWhiteSpace(d.Reference)) Center(Para(s.AddParagraph(), d.Reference!, 8, bold: true, color: accent));
        if (!string.IsNullOrWhiteSpace(d.Subtitle)) Center(Para(s.AddParagraph(), d.Subtitle!, 6.5, color: Muted));
        Stamp(s, d.Stamp, 10);

        foreach (var party in d.Parties ?? [])
        {
            Para(s.AddParagraph(), party.Label.ToUpperInvariant(), 6.5, bold: true, color: Muted).Format.SpaceBefore = Unit.FromPoint(3);
            for (var k = 0; k < party.Lines.Count; k++) Para(s.AddParagraph(), party.Lines[k], 7.5, bold: k == 0);
        }

        if (d.Details is { Count: > 0 } details)
        {
            Spacer(s, 3);
            KeyValues(s, details.Select(f => (f.Label, PrintValues.Text(f.Value, f.Kind, f.Currency, tz, withCurrency: true), f.Emphasis)), 7);
        }

        foreach (var pt in d.Tables ?? [])
        {
            Dotted(s);
            if (!string.IsNullOrWhiteSpace(pt.Title)) Para(s.AddParagraph(), pt.Title!, 7.5, bold: true);
            if (FitsTicketGrid(pt)) TicketGrid(s, pt, tz);
            else TicketStacked(s, pt, tz);
        }

        if (d.Summary is { Count: > 0 } summary)
        {
            Dotted(s);
            var t = s.AddTable();
            t.Borders.Visible = false;
            t.TopPadding = Unit.FromPoint(1.5);
            t.BottomPadding = Unit.FromPoint(1.5);
            t.LeftPadding = Unit.FromPoint(0);
            t.RightPadding = Unit.FromPoint(0);
            t.AddColumn(Unit.FromCentimeter(TicketWidthCm * 0.55));
            t.AddColumn(Unit.FromCentimeter(TicketWidthCm * 0.45)).Format.Alignment = ParagraphAlignment.Right;
            foreach (var f in summary)
            {
                var r = t.AddRow();
                Para(r.Cells[0].AddParagraph(), f.Label, f.Emphasis ? 8.5 : 7.5, bold: f.Emphasis);
                Para(r.Cells[1].AddParagraph(), PrintValues.Text(f.Value, f.Kind, f.Currency, tz, withCurrency: true), f.Emphasis ? 9 : 7.5, bold: true);
                if (f.Emphasis)
                {
                    r.Borders.Top.Width = 0.8;
                    r.Borders.Top.Color = Ink;
                }
            }
        }

        foreach (var note in d.Notes ?? []) Para(s.AddParagraph(), note, 6.5, color: Muted).Format.SpaceBefore = Unit.FromPoint(3);

        foreach (var label in d.Signatures ?? [])
        {
            var p = Center(Para(s.AddParagraph(), label, 6.5, color: Muted));
            p.Format.SpaceBefore = Unit.FromPoint(22);
            p.Format.Borders.Top.Width = 0.5;
            p.Format.Borders.Top.Color = Ink;
        }

        Dotted(s);
        if (!string.IsNullOrWhiteSpace(iss.Footer)) Center(Para(s.AddParagraph(), iss.Footer!, 6.5));
        Center(Para(s.AddParagraph(), PrintedLine(meta, tz), 6, color: Muted));
        if (!d.Official) Center(Para(s.AddParagraph(), NotContractual, 6, bold: true, color: Muted));
    }

    private static void TicketGrid(Section s, PrintTable pt, TimeZoneInfo tz)
    {
        var cols = pt.Columns.Count;
        if (cols == 0) return;
        var weights = pt.Columns.Select(TicketWeight).ToArray();
        var total = weights.Sum();
        var t = s.AddTable();
        t.Borders.Visible = false;
        t.Format.Font.Size = 7;
        t.TopPadding = Unit.FromPoint(1);
        t.BottomPadding = Unit.FromPoint(1);
        t.LeftPadding = Unit.FromPoint(0);
        t.RightPadding = Unit.FromPoint(1.5);
        for (var i = 0; i < cols; i++)
            t.AddColumn(Unit.FromCentimeter(TicketWidthCm * weights[i] / total)).Format.Alignment =
                PrintValues.IsNumeric(pt.Columns[i].Kind) ? ParagraphAlignment.Right : ParagraphAlignment.Left;
        var h = t.AddRow();
        h.Format.Font.Bold = true;
        h.Borders.Bottom.Width = 0.5;
        h.Borders.Bottom.Color = Ink;
        for (var i = 0; i < cols; i++) h.Cells[i].AddParagraph(PrintValues.Header(pt.Columns[i]));
        foreach (var row in pt.Rows)
        {
            var r = t.AddRow();
            for (var i = 0; i < cols; i++)
                r.Cells[i].AddParagraph(PrintValues.Text(i < row.Count ? row[i] : null, pt.Columns[i].Kind, pt.Columns[i].Currency, tz));
        }
        if (pt.Totals is { } totals)
        {
            var r = t.AddRow();
            r.Format.Font.Bold = true;
            r.Borders.Top.Width = 0.5;
            r.Borders.Top.Color = Ink;
            for (var i = 0; i < cols; i++)
                r.Cells[i].AddParagraph(PrintValues.Text(i < totals.Count ? totals[i] : null, pt.Columns[i].Kind, pt.Columns[i].Currency, tz));
        }
    }

    /// <summary>Tient en colonnes sur 72 mm : 3 colonnes, ou 4 dont un seul texte (article, qté, P.U., montant).</summary>
    private static bool FitsTicketGrid(PrintTable t) =>
        t.Columns.Count <= 3 || (t.Columns.Count == 4 && t.Columns.Count(c => c.Kind == PrintKinds.Text) <= 1);

    /// <summary>Tableau trop large pour 72 mm : une ligne = un bloc « libellé : valeur ».</summary>
    private static void TicketStacked(Section s, PrintTable pt, TimeZoneInfo tz)
    {
        foreach (var row in pt.Rows)
        {
            var first = Para(s.AddParagraph(), PrintValues.Text(row.Count > 0 ? row[0] : null, pt.Columns[0].Kind, pt.Columns[0].Currency, tz), 7.5, bold: true);
            first.Format.SpaceBefore = Unit.FromPoint(3);
            KeyValues(s, pt.Columns.Skip(1).Select((c, i) =>
                (PrintValues.Header(c), PrintValues.Text(i + 1 < row.Count ? row[i + 1] : null, c.Kind, c.Currency, tz), false)), 6.8);
        }
        if (pt.Totals is { } totals)
            KeyValues(s, pt.Columns.Select((c, i) => (c, i))
                .Where(x => x.i < totals.Count && totals[x.i] is not null)
                .Select(x => (PrintValues.Header(x.c), PrintValues.Text(totals[x.i], x.c.Kind, x.c.Currency, tz), true)), 7);
    }

    private static void KeyValues(Section s, IEnumerable<(string Label, string Value, bool Bold)> pairs, double size)
    {
        var t = s.AddTable();
        t.Borders.Visible = false;
        t.TopPadding = Unit.FromPoint(0.8);
        t.BottomPadding = Unit.FromPoint(0.8);
        t.LeftPadding = Unit.FromPoint(0);
        t.RightPadding = Unit.FromPoint(0);
        t.AddColumn(Unit.FromCentimeter(TicketWidthCm * 0.45));
        t.AddColumn(Unit.FromCentimeter(TicketWidthCm * 0.55)).Format.Alignment = ParagraphAlignment.Right;
        foreach (var (label, value, bold) in pairs)
        {
            var r = t.AddRow();
            Para(r.Cells[0].AddParagraph(), label, size, color: Muted);
            Para(r.Cells[1].AddParagraph(), value, size, bold: bold);
        }
    }

    // ------------------------------------------------------------------------------ Outils

    private const string NotContractual = "Export de données affichées à l'écran — document non contractuel.";

    private static IEnumerable<string> IssuerLines(PrintIssuer iss)
    {
        if (!string.IsNullOrWhiteSpace(iss.LegalName) && !string.Equals(iss.LegalName, iss.Name, StringComparison.OrdinalIgnoreCase))
            yield return iss.LegalName!;
        if (!string.IsNullOrWhiteSpace(iss.TaxId)) yield return $"NIF : {iss.TaxId}";
        if (!string.IsNullOrWhiteSpace(iss.Address)) yield return iss.Address!;
        if (!string.IsNullOrWhiteSpace(iss.Phone)) yield return $"Tél. : {iss.Phone}";
    }

    private static string PrintedLine(PrintMeta meta, TimeZoneInfo tz)
    {
        var at = TimeZoneInfo.ConvertTime(meta.PrintedAt, tz).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(meta.PrintedBy) ? $"Imprimé le {at}" : $"Imprimé le {at} par {meta.PrintedBy}";
    }

    private static void Stamp(Section s, string? stamp, double size)
    {
        if (string.IsNullOrWhiteSpace(stamp)) return;
        var p = Center(Para(s.AddParagraph(), stamp, size, bold: true, color: StampRed));
        p.Format.Borders.Width = 1.2;
        p.Format.Borders.Color = StampRed;
        p.Format.Borders.Distance = Unit.FromPoint(3);
        p.Format.SpaceBefore = Unit.FromPoint(4);
        p.Format.SpaceAfter = Unit.FromPoint(8);
    }

    private static double Weight(PrintColumn c) => c.Kind switch
    {
        PrintKinds.Text => 2.2,
        PrintKinds.Money => 1.25,
        PrintKinds.Number => 1.0,
        PrintKinds.Integer => 0.8,
        PrintKinds.Percent => 0.8,
        PrintKinds.Date => 1.0,
        PrintKinds.DateTime => 1.35,
        _ => 1.5,
    };

    /// <summary>Largeurs sur 72 mm : une date « 26/06/2026 » ne doit jamais déborder sur la colonne voisine.</summary>
    private static double TicketWeight(PrintColumn c) => c.Kind switch
    {
        PrintKinds.Date => 1.5,
        PrintKinds.DateTime => 2.0,
        _ => Weight(c),
    };

    private static Paragraph Para(Paragraph p, string text, double size, bool bold = false, Color? color = null)
    {
        p.AddText(text);
        p.Format.Font.Size = size;
        p.Format.Font.Bold = bold;
        if (color is { } c) p.Format.Font.Color = c;
        return p;
    }

    private static Paragraph Center(Paragraph p)
    {
        p.Format.Alignment = ParagraphAlignment.Center;
        return p;
    }

    private static void Spacer(Section s, double points) => s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(points);

    private static void Dotted(Section s)
    {
        var p = s.AddParagraph();
        p.Format.Font.Size = 2;
        p.Format.Borders.Bottom.Width = 0.6;
        p.Format.Borders.Bottom.Style = BorderStyle.Dot;
        p.Format.Borders.Bottom.Color = Muted;
        p.Format.SpaceAfter = Unit.FromPoint(4);
    }

    private static bool IsHex(string? v) =>
        v is { Length: 7 } && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit);

    private static Color Hex(string hex) => new(
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    /// <summary>Couleur de marque lisible sur papier blanc : une teinte trop claire (le turquoise #00F5D4
    /// de l'interface sombre) est assombrie jusqu'à un contraste suffisant.</summary>
    private static Color PrintAccent(Color c)
    {
        var color = c;
        for (var i = 0; i < 12 && Luminance(color) > 0.28; i++)
            color = new Color((byte)(color.R * 0.85), (byte)(color.G * 0.85), (byte)(color.B * 0.85));
        return color;
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel((byte)c.R) + 0.7152 * Channel((byte)c.G) + 0.0722 * Channel((byte)c.B);
    }

    /// <summary>Teinte claire d'une couleur (mélange avec du blanc).</summary>
    private static Color Tint(Color c, double white) => new(
        (byte)(c.R + (255 - c.R) * white), (byte)(c.G + (255 - c.G) * white), (byte)(c.B + (255 - c.B) * white));
}
