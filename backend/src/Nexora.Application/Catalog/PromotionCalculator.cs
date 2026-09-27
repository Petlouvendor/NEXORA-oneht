namespace Nexora.Application.Catalog;

/// <summary>
/// Promotion applicable à une ligne, telle que lue en base (<c>nexora.promotion</c>, V018).
/// <paramref name="Value"/> : pourcentage (percentage), montant unitaire (amount), prix unitaire
/// imposé (price_override) ou prix du lot (bundle) ; ignoré pour buy_x_get_y.
/// </summary>
public sealed record PromotionRule(
    Guid Id, string Name, string PromoType, decimal Value, PromotionConditions Conditions);

/// <summary>
/// Conditions d'une promotion (colonne <c>conditions</c> jsonb) :
/// <c>minQuantity</c> (toutes), <c>buyQuantity</c> + <c>getQuantity</c> (buy_x_get_y : « 2 pour 1 »
/// = 1 acheté + 1 offert), <c>bundleQuantity</c> (bundle : N unités pour <c>value</c>).
/// </summary>
public sealed record PromotionConditions(
    decimal? MinQuantity = null, decimal? BuyQuantity = null, decimal? GetQuantity = null, decimal? BundleQuantity = null);

/// <summary>
/// Calcul unique des promotions (finalisation produit, phase 1). Avant, l'encaissement et le simulateur
/// de prix avaient chacun leur propre calcul — le simulateur pouvait annoncer un prix que la caisse
/// n'appliquait pas. Fonction pure et déterministe : même entrée, même remise ; la remise ne dépasse
/// jamais le montant de la ligne. Seules des unités entières complètes déclenchent un lot ou une offre.
/// </summary>
public static class PromotionCalculator
{
    public static readonly string[] Types = ["percentage", "amount", "price_override", "buy_x_get_y", "bundle"];

    /// <param name="unitNet">Prix unitaire hors taxe retenu pour la ligne.</param>
    /// <param name="lineNet">Montant de la ligne avant remise (<c>unitNet × quantity</c> arrondi).</param>
    public static decimal Discount(PromotionRule promo, decimal unitNet, decimal quantity, decimal lineNet)
    {
        if (quantity <= 0m || lineNet <= 0m) return 0m;
        var c = promo.Conditions;
        if (c.MinQuantity is { } min && quantity < min) return 0m;

        var d = promo.PromoType switch
        {
            "percentage" => R2(lineNet * promo.Value / 100m),
            "amount" => R2(promo.Value * quantity),
            "price_override" => lineNet - R2(promo.Value * quantity),
            "buy_x_get_y" => BuyXGetY(c, unitNet, quantity),
            "bundle" => Bundle(c, promo.Value, unitNet, quantity),
            _ => 0m,
        };
        return Math.Clamp(d, 0m, lineNet);
    }

    private static decimal BuyXGetY(PromotionConditions c, decimal unitNet, decimal quantity)
    {
        if (c.BuyQuantity is not { } buy || c.GetQuantity is not { } get || buy <= 0m || get <= 0m) return 0m;
        var groups = Math.Floor(quantity / (buy + get));
        return R2(groups * get * unitNet);
    }

    private static decimal Bundle(PromotionConditions c, decimal bundlePrice, decimal unitNet, decimal quantity)
    {
        if (c.BundleQuantity is not { } size || size <= 0m || bundlePrice < 0m) return 0m;
        var groups = Math.Floor(quantity / size);
        return R2(groups * (size * unitNet - bundlePrice));
    }

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);
}
