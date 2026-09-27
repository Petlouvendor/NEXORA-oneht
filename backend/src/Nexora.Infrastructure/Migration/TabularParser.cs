using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexora.Infrastructure.Migration;

/// <summary>Fichier tabulaire parsé : en-têtes + lignes (clé = en-tête, valeur = cellule brute).</summary>
internal sealed record ParsedTable(IReadOnlyList<string> Headers, IReadOnlyList<Dictionary<string, string?>> Rows);

/// <summary>
/// Lecture CSV (RFC 4180 — guillemets, séparateurs échappés, retours à la ligne dans les cellules ;
/// délimiteur auto-détecté <c>, ; \t |</c> ; BOM UTF-8 retiré) et JSON (tableau d'objets plats).
/// Aucune dépendance externe (§44 CSV UTF-8 / JSON).
/// </summary>
internal static partial class TabularParser
{
    public static ParsedTable Parse(string sourceType, string content)
    {
        content ??= "";
        if (content.Length > 0 && content[0] == '﻿') content = content[1..];

        return sourceType switch
        {
            "json" => ParseJson(content),
            _ => ParseCsv(content),
        };
    }

    // ------------------------------------------------------------- CSV

    private static ParsedTable ParseCsv(string content)
    {
        var delimiter = DetectDelimiter(content);
        var records = SplitRecords(content, delimiter);
        if (records.Count == 0) return new ParsedTable([], []);

        var headers = records[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"col{i + 1}" : h.Trim()).ToList();
        // Dé-doublonne les en-têtes.
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < headers.Count; i++)
        {
            var h = headers[i];
            if (seen.TryGetValue(h, out var n)) { seen[h] = n + 1; headers[i] = $"{h}_{n + 1}"; }
            else seen[h] = 1;
        }

        var rows = new List<Dictionary<string, string?>>(records.Count - 1);
        for (var r = 1; r < records.Count; r++)
        {
            var cells = records[r];
            if (cells.Count == 1 && string.IsNullOrWhiteSpace(cells[0])) continue;   // ligne vide
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Count; c++)
                row[headers[c]] = c < cells.Count ? EmptyToNull(cells[c]) : null;
            rows.Add(row);
        }
        return new ParsedTable(headers, rows);
    }

    private static char DetectDelimiter(string content)
    {
        var firstLine = content.Split('\n', 2)[0];
        char[] candidates = [',', ';', '\t', '|'];
        return candidates.OrderByDescending(d => firstLine.Count(ch => ch == d)).First();
    }

    private static List<List<string>> SplitRecords(string content, char delimiter)
    {
        var records = new List<List<string>>();
        var current = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '"': inQuotes = true; break;
                    case '\r': break;
                    case '\n':
                        current.Add(field.ToString()); field.Clear();
                        records.Add(current); current = [];
                        break;
                    default:
                        if (ch == delimiter) { current.Add(field.ToString()); field.Clear(); }
                        else field.Append(ch);
                        break;
                }
            }
        }
        if (field.Length > 0 || current.Count > 0) { current.Add(field.ToString()); records.Add(current); }
        return records;
    }

    // ------------------------------------------------------------- JSON

    private static ParsedTable ParseJson(string content)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "[]" : content);
        var root = doc.RootElement;
        var arr = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array => d,
            JsonValueKind.Object => WrapSingle(root),
            _ => throw new FormatException("JSON attendu : un tableau d'objets."),
        };

        var headers = new List<string>();
        var rows = new List<Dictionary<string, string?>>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in el.EnumerateObject())
            {
                if (!headers.Contains(p.Name)) headers.Add(p.Name);
                row[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => EmptyToNull(p.Value.GetString()),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    _ => p.Value.GetRawText(),
                };
            }
            rows.Add(row);
        }
        return new ParsedTable(headers, rows);
    }

    private static JsonElement WrapSingle(JsonElement obj)
    {
        using var d = JsonDocument.Parse($"[{obj.GetRawText()}]");
        return d.RootElement.Clone();
    }

    // ------------------------------------------------------------- Transformations (§8)

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy", "dd.MM.yyyy",
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "d/M/yyyy", "M/d/yyyy",
    ];

    /// <summary>Applique une transformation nommée à une valeur brute (§8).</summary>
    public static string? ApplyTransform(string? value, string? transform)
    {
        if (value is null) return null;
        var v = value;
        switch ((transform ?? "trim").Trim().ToLowerInvariant())
        {
            case "" or "none": return value;
            case "trim": return v.Trim().Length == 0 ? null : v.Trim();
            case "lower": return v.Trim().ToLowerInvariant();
            case "upper": return v.Trim().ToUpperInvariant();
            case "email": return v.Trim().ToLowerInvariant().Length == 0 ? null : v.Trim().ToLowerInvariant();
            case "phone":
                var digits = PhoneChars().Replace(v, "");
                return digits.Length == 0 ? null : (v.TrimStart().StartsWith('+') ? "+" : "") + digits;
            case "date":
                var d = ParseDate(v);
                return d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case "decimal":
            case "number":
                var n = ParseDecimal(v);
                return n?.ToString(CultureInfo.InvariantCulture);
            case "integer":
                var iv = ParseDecimal(v);
                return iv is null ? null : ((long)Math.Round(iv.Value)).ToString(CultureInfo.InvariantCulture);
            case "boolean":
                return ParseBool(v)?.ToString().ToLowerInvariant();
            default: return v.Trim();
        }
    }

    public static DateOnly? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateOnly.TryParseExact(s[..Math.Min(10, s.Length)], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso;
        foreach (var f in DateFormats)
            if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return DateOnly.FromDateTime(dt);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var any)
            ? DateOnly.FromDateTime(any) : null;
    }

    public static decimal? ParseDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Replace(" ", "").Replace(" ", "");
        // Ne garde que chiffres, séparateurs et signe.
        t = DecimalChars().Replace(t, "");
        if (t.Length == 0) return null;

        var lastComma = t.LastIndexOf(',');
        var lastDot = t.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
            t = lastComma > lastDot ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        else if (lastComma >= 0)
            t = t.Count(c => c == ',') > 1 ? t.Replace(",", "") : t.Replace(',', '.');

        return decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public static bool? ParseBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "oui" or "y" or "o" or "vrai" or "x" => true,
            "0" or "false" or "no" or "non" or "n" or "faux" or "" => false,
            _ => null,
        };
    }

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [GeneratedRegex(@"[^0-9]")] private static partial Regex PhoneChars();
    [GeneratedRegex(@"[^0-9,.\-]")] private static partial Regex DecimalChars();
}
