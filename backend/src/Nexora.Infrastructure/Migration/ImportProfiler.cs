using System.Text.RegularExpressions;
using Nexora.Contracts.Migration;

namespace Nexora.Infrastructure.Migration;

/// <summary>Profilage d'un fichier importé (Phase 39 §6) : types, valeurs manquantes, doublons, formats.</summary>
internal static partial class ImportProfiler
{
    public static ImportProfileDto Profile(ParsedTable table, DateTimeOffset now)
    {
        var cols = new List<ColumnProfileDto>();
        foreach (var name in table.Headers)
        {
            var values = table.Rows.Select(r => r.GetValueOrDefault(name)).ToList();
            var nonEmpty = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
            var distinct = nonEmpty.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var type = DetectType(nonEmpty);
            var invalid = type switch
            {
                "integer" or "decimal" => nonEmpty.Count(v => TabularParser.ParseDecimal(v) is null),
                "date" => nonEmpty.Count(v => TabularParser.ParseDate(v) is null),
                "email" => nonEmpty.Count(v => !EmailRe().IsMatch(v)),
                _ => 0,
            };
            cols.Add(new ColumnProfileDto(name, type, nonEmpty.Count, values.Count - nonEmpty.Count,
                distinct.Count, distinct.Take(5).ToList(), invalid));
        }

        // Doublons : lignes strictement identiques sur l'ensemble des colonnes.
        var dupRows = table.Rows
            .GroupBy(r => string.Join("", table.Headers.Select(h => r.GetValueOrDefault(h)?.Trim().ToLowerInvariant() ?? "")))
            .Where(g => g.Count() > 1)
            .Sum(g => g.Count() - 1);

        var currency = DetectCurrency(table);
        return new ImportProfileDto(table.Rows.Count, table.Headers.ToList(), cols, dupRows, currency, now);
    }

    private static string DetectType(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return "empty";
        bool all(Func<string, bool> p) => values.All(p);
        if (all(v => v is "0" or "1" || (TabularParser.ParseBool(v) is not null && v.Length <= 5))) return "boolean";
        if (all(v => EmailRe().IsMatch(v))) return "email";
        // Détection stricte : la valeur brute doit *ressembler* à un nombre (pas seulement être coercible).
        if (all(v => IntRe().IsMatch(v.Replace(" ", "")))) return "integer";
        if (all(v => NumRe().IsMatch(v.Replace(" ", "")) && TabularParser.ParseDecimal(v) is not null)) return "decimal";
        if (all(v => TabularParser.ParseDate(v) is not null)) return "date";
        if (all(v => PhoneRe().IsMatch(v))) return "phone";
        return "text";
    }

    private static string? DetectCurrency(ParsedTable table)
    {
        var col = table.Headers.FirstOrDefault(h => Regex.IsMatch(h, "(?i)devise|currency|monnaie"));
        if (col is null) return null;
        var v = table.Rows.Select(r => r.GetValueOrDefault(col)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        return v?.Trim().ToUpperInvariant();
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailRe();
    [GeneratedRegex(@"^[+()\-.\s0-9]{6,}$")] private static partial Regex PhoneRe();
    [GeneratedRegex(@"^[+\-]?\d{1,3}([.,]?\d{3})*$")] private static partial Regex IntRe();
    [GeneratedRegex(@"^[+\-]?\d[\d.,]*$")] private static partial Regex NumRe();
}
