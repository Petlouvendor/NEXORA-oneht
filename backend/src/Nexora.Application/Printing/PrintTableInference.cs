using System.Globalization;
using System.Text.RegularExpressions;
using Nexora.Contracts.Printing;

namespace Nexora.Application.Printing;

/// <summary>
/// Tableau de chaînes (rapports existants) → tableau typé : une colonne dont toutes les valeurs sont des
/// nombres invariants devient numérique (montant si 2 décimales), une colonne de dates ISO devient date.
/// Les colonnes d'identifiants (code, n°, téléphone, NIF) restent du texte, même si elles sont chiffrées.
/// </summary>
public static partial class PrintTableInference
{
    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")]
    private static partial Regex DateTimePattern();

    private static readonly string[] TextHints = ["code", "n°", "numéro", "numero", "tél", "tel", "phone", "nif", "référence", "reference", "réf"];

    public static PrintTable Build(string? title, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var columns = headers.Select((h, i) => new PrintColumn(h, Infer(h, rows.Select(r => i < r.Count ? r[i] : null)))).ToList();
        var typed = rows.Select(r => (IReadOnlyList<object?>)columns.Select((c, i) => Convert(i < r.Count ? r[i] : null, c.Kind)).ToList()).ToList();
        return new PrintTable(title, columns, typed);
    }

    private static string Infer(string header, IEnumerable<string?> values)
    {
        var h = header.ToLowerInvariant();
        if (TextHints.Any(h.Contains)) return PrintKinds.Text;
        var present = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (present.Count == 0) return PrintKinds.Text;
        if (present.All(v => DatePattern().IsMatch(v))) return PrintKinds.Date;
        if (present.All(v => DateTimePattern().IsMatch(v))) return PrintKinds.DateTime;
        if (!present.All(v => NumberPattern().IsMatch(v))) return PrintKinds.Text;
        if (present.Any(v => v.Contains('.') && v.Length - v.IndexOf('.') - 1 == 2)) return PrintKinds.Money;
        return present.All(v => !v.Contains('.')) ? PrintKinds.Integer : PrintKinds.Number;
    }

    private static object? Convert(string? value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return kind switch
        {
            PrintKinds.Money or PrintKinds.Number or PrintKinds.Integer
                when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => value,
        };
    }
}
