namespace Nexora.Domain.Accounting;

/// <summary>
/// Arrondi de conversion (audit 2026-10 éd. 2, A-041). Chaque ligne en devise est convertie puis arrondie séparément :
/// 10,01 + 10,01 USD à 132,5 donnent 1 326,33 + 1 326,33 = 2 652,66 HTG en base, contre 2 652,65 pour les 20,02 USD
/// d'en face — une écriture juste en devise refusée pour un centime. Pour chaque devise étrangère dont les lignes
/// s'équilibrent exactement en devise, l'écart de base (au plus un demi-centime par ligne) est porté sur la plus grosse
/// ligne du côté le plus faible. Un écart plus grand n'est pas un arrondi : il est laissé, et le contrôle d'équilibre
/// refuse l'écriture.
/// </summary>
public static class ConversionRounding
{
    /// <summary>Lignes adaptées par l'appelant : devise, montants en devise et en base, et écriture des bases ajustées.</summary>
    public interface ILine
    {
        string Currency { get; }
        decimal Debit { get; }
        decimal Credit { get; }
        decimal BaseDebit { get; set; }
        decimal BaseCredit { get; set; }
    }

    public static void Absorb(IReadOnlyList<ILine> lines, string functionalCurrency, int functionalDecimals = 2)
    {
        var unit = 1m / (decimal)Math.Pow(10, functionalDecimals);
        foreach (var group in lines.Where(l => !string.Equals(l.Currency, functionalCurrency, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(l => l.Currency.ToUpperInvariant()))
        {
            var g = group.ToList();
            if (g.Sum(l => l.Debit) != g.Sum(l => l.Credit)) continue;   // déséquilibre réel en devise : pas un arrondi
            var diff = g.Sum(l => l.BaseDebit) - g.Sum(l => l.BaseCredit);
            if (diff == 0m || Math.Abs(diff) > Math.Max(unit, unit / 2m * g.Count)) continue;

            if (diff < 0m)
            {
                var target = g.Where(l => l.BaseDebit > 0m).MaxBy(l => l.BaseDebit);
                if (target is not null) target.BaseDebit += -diff;
            }
            else
            {
                var target = g.Where(l => l.BaseCredit > 0m).MaxBy(l => l.BaseCredit);
                if (target is not null) target.BaseCredit += diff;
            }
        }
    }
}
