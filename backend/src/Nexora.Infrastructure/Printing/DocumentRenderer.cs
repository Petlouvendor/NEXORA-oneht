using System.Globalization;
using System.Text;
using Nexora.Application.Printing;
using Nexora.Contracts.Printing;

namespace Nexora.Infrastructure.Printing;

/// <summary>Aiguillage par format : PDF A4, ticket 80 mm, Excel (.xlsx) ou CSV.</summary>
public sealed class DocumentRenderer : IDocumentRenderer
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public RenderedFile Render(PrintDocument document, PrintIssuer issuer, PrintMeta meta, string format, string fileBaseName)
    {
        var name = PrintValues.SafeFileName(fileBaseName);
        return PrintFormats.Normalize(format) switch
        {
            PrintFormats.Pdf => new($"{name}.pdf", "application/pdf", PdfDocumentWriter.Write(document, issuer, meta, ticket: false)),
            PrintFormats.Ticket => new($"{name}-ticket.pdf", "application/pdf", PdfDocumentWriter.Write(document, issuer, meta, ticket: true)),
            PrintFormats.Xlsx => new($"{name}.xlsx", XlsxContentType, XlsxDocumentWriter.Write(document, issuer, meta)),
            PrintFormats.Csv => new($"{name}.csv", "text/csv; charset=utf-8", Csv(document, PrintValues.Zone(issuer.TimeZone))),
            var f => throw new ArgumentOutOfRangeException(nameof(format), f, "Format inconnu (pdf, ticket, xlsx, csv)."),
        };
    }

    /// <summary>CSV « ; » avec BOM (accents lisibles par Excel), nombres au point décimal, dates ISO.
    /// Une valeur texte commençant par = + - @ est neutralisée (injection de formule).</summary>
    internal static byte[] Csv(PrintDocument d, TimeZoneInfo tz)
    {
        var sb = new StringBuilder();
        var tables = d.Tables ?? [];
        if (tables.Count == 0)
        {
            foreach (var f in (d.Details ?? []).Concat(d.Summary ?? []))
                sb.Append(Escape(f.Label)).Append(';').AppendLine(CsvValue(f.Value, f.Kind, tz));
        }
        foreach (var t in tables)
        {
            if (tables.Count > 1)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(Escape(t.Title ?? ""));
            }
            sb.AppendLine(string.Join(";", t.Columns.Select(c => Escape(PrintValues.Header(c)))));
            foreach (var row in t.Rows)
                sb.AppendLine(string.Join(";", t.Columns.Select((c, i) => CsvValue(i < row.Count ? row[i] : null, c.Kind, tz))));
            if (t.Totals is { } totals)
                sb.AppendLine(string.Join(";", t.Columns.Select((c, i) => CsvValue(i < totals.Count ? totals[i] : null, c.Kind, tz))));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string CsvValue(object? value, string kind, TimeZoneInfo tz)
    {
        var v = PrintValues.Unwrap(value);
        if (v is null) return "";
        if (PrintValues.IsNumeric(kind) && PrintValues.AsDecimal(v) is { } n)
            return n.ToString(kind == PrintKinds.Money ? "0.00" : "0.###", CultureInfo.InvariantCulture);
        if (kind == PrintKinds.Date && PrintValues.AsDate(v, tz) is { } d)
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (kind == PrintKinds.DateTime && PrintValues.AsLocalDateTime(v, tz) is { } dt)
            return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (v is decimal or double or int or long)
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        return Escape(PrintValues.Text(v, PrintKinds.Text, null, tz));
    }

    private static string Escape(string v)
    {
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return v.Contains(';') || v.Contains('"') || v.Contains('\n') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }
}
