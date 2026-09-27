using System.Globalization;
using System.Text.Json;
using Nexora.Contracts.Printing;

namespace Nexora.Infrastructure.Printing;

/// <summary>
/// Normalisation et mise en forme des valeurs d'un document imprimable. Indépendant de la culture du
/// serveur (l'image Linux peut tourner sans ICU) : séparateur décimal virgule, milliers en espace
/// insécable, dates jj/mm/aaaa dans le fuseau de l'entreprise.
/// </summary>
internal static class PrintValues
{
    public static readonly NumberFormatInfo Fr = new()
    {
        NumberDecimalSeparator = ",", NumberGroupSeparator = " ", NumberGroupSizes = [3],
        PercentDecimalSeparator = ",", PercentGroupSeparator = " ",
    };

    public static object? Unwrap(object? v) => v is JsonElement je
        ? je.ValueKind switch
        {
            JsonValueKind.Number => je.TryGetDecimal(out var d) ? d : je.GetDouble(),
            JsonValueKind.String => je.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => je.ToString(),
        }
        : v;

    public static decimal? AsDecimal(object? v) => Unwrap(v) switch
    {
        decimal d => d,
        double d when !double.IsNaN(d) && !double.IsInfinity(d) => (decimal)d,
        float f when !float.IsNaN(f) && !float.IsInfinity(f) => (decimal)f,
        int i => i,
        long l => l,
        short s => s,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>Date civile (sans heure) : DateOnly, « aaaa-mm-jj », ou instant ramené au fuseau.</summary>
    public static DateOnly? AsDate(object? v, TimeZoneInfo tz) => Unwrap(v) switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        DateTimeOffset o => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o, tz).DateTime),
        string s when DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o)
            => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o, tz).DateTime),
        _ => null,
    };

    /// <summary>Instant exprimé dans le fuseau de l'entreprise (heure murale).</summary>
    public static DateTime? AsLocalDateTime(object? v, TimeZoneInfo tz) => Unwrap(v) switch
    {
        DateTimeOffset o => TimeZoneInfo.ConvertTime(o, tz).DateTime,
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var o)
            => TimeZoneInfo.ConvertTime(o, tz).DateTime,
        _ => null,
    };

    public static string Money(decimal d) => d.ToString("#,##0.00", Fr);

    public static string Text(object? value, string kind, string? currency, TimeZoneInfo tz, bool withCurrency = false)
    {
        var v = Unwrap(value);
        if (v is null) return "";
        switch (kind)
        {
            case PrintKinds.Money when AsDecimal(v) is { } m:
                return withCurrency && !string.IsNullOrEmpty(currency) ? $"{Money(m)} {currency}" : Money(m);
            case PrintKinds.Number when AsDecimal(v) is { } n:
                return n.ToString("#,##0.###", Fr);
            case PrintKinds.Integer when AsDecimal(v) is { } i:
                return Math.Round(i).ToString("#,##0", Fr);
            case PrintKinds.Percent when AsDecimal(v) is { } p:
                return p.ToString("#,##0.#", Fr) + " %";
            case PrintKinds.Date when AsDate(v, tz) is { } d:
                return d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            case PrintKinds.DateTime when AsLocalDateTime(v, tz) is { } dt:
                return dt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        }
        return v switch
        {
            bool b => b ? "Oui" : "Non",
            decimal d => d.ToString("#,##0.##", Fr),
            double d => d.ToString("#,##0.##", Fr),
            DateOnly d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            DateTimeOffset o => TimeZoneInfo.ConvertTime(o, tz).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        };
    }

    public static bool IsNumeric(string kind) =>
        kind is PrintKinds.Money or PrintKinds.Number or PrintKinds.Integer or PrintKinds.Percent;

    public static TimeZoneInfo Zone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Libellé de colonne avec sa devise : « Montant (HTG) ».</summary>
    public static string Header(PrintColumn c) =>
        c.Kind == PrintKinds.Money && !string.IsNullOrEmpty(c.Currency) ? $"{c.Label} ({c.Currency})" : c.Label;

    /// <summary>Nom de fichier sûr (sans accents ni caractères spéciaux).</summary>
    public static string SafeFileName(string name)
    {
        var normalized = name.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? char.ToLowerInvariant(ch) : '-')
            .ToArray();
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        s = s.Trim('-');
        return string.IsNullOrEmpty(s) ? "document" : s.Length > 80 ? s[..80] : s;
    }
}
