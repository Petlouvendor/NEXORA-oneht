using Nexora.Domain.Common;

namespace Nexora.Application.Sales;

/// <summary>Prix résolu pour une ligne (Phase 6 §9-10). Le résultat est figé dans la vente.</summary>
public sealed record PricedLine(
    Guid ProductId, decimal Quantity,
    decimal UnitPrice, decimal ManualDiscount,
    decimal PromotionDiscount, string? PromotionLabel, Guid? PromotionId, Guid? CouponId,
    decimal TaxRate, Guid? TaxId, decimal UnitCost, string Description);

public interface ISalesPricing
{
    /// <summary>Recalcul serveur déterministe (§12, §19) : prix + promotions + taxe par ligne.</summary>
    Task<IReadOnlyList<PricedLine>> PriceCartAsync(
        Guid inventoryLocationId, Guid? customerId, string currency, DateTimeOffset at,
        IReadOnlyList<CheckoutLine> lines, string? couponCode, CancellationToken ct = default);
}

/// <summary>
/// Garde-fou des montants saisis par le client (recette Phase 55 POS-006, FIN-014 — défaut
/// critique : le prix forcé et la remise manuelle étaient acceptés tels quels). Vérifie, après le
/// recalcul serveur : quantité &gt; 0, montants non négatifs, remise ≤ montant de la ligne,
/// prix forcé ⇒ <c>pos.change_price</c>, remise ⇒ <c>pos.discount</c> et ≤ la limite de remise
/// de l'utilisateur (guide §12 : limite du rôle, bornée par <c>pos.max_discount_percent</c>) sauf
/// remise exceptionnelle approuvée pour ce panier, prix net unitaire ≥ prix plancher (<c>min_price</c>).
/// Les prix figés d'une commande ou d'un devis déjà contrôlés à leur création ne repassent pas ici.
/// </summary>
public interface ISalePricingGuard
{
    /// <summary>Guide §9, §25 : seul un produit existant, actif et vendable part en vente —
    /// contrôlé avant tout calcul de prix.</summary>
    Task<Result> ValidateProductsAsync(IReadOnlyList<CheckoutLine> requested, CancellationToken ct = default);

    /// <param name="approvalOperation">UUID d'opération du panier : les remises exceptionnelles
    /// approuvées pour ce panier (et ce vendeur) lèvent la limite. <c>null</c> = aucune approbation
    /// possible (commande, devis).</param>
    Task<Result> ValidateAsync(IReadOnlyList<CheckoutLine> requested, IReadOnlyList<PricedLine> priced,
        string currency, DateTimeOffset at, Guid? approvalOperation, CancellationToken ct = default);

    /// <summary>Consomme les approbations de remise du panier au profit de la vente (usage unique) —
    /// appelé dans la transaction de vente.</summary>
    Task ConsumeDiscountApprovalsAsync(Guid operationUuid, Guid saleId, CancellationToken ct = default);
}

/// <summary>Limites de remise de l'utilisateur courant (guide §8, §12). <c>null</c> = pas de limite.
/// <paramref name="SelfLimitPercent"/> : remise qu'il applique lui-même (limite de son rôle bornée
/// par le plafond d'entreprise) ; <paramref name="ApprovalLimitPercent"/> : remise exceptionnelle
/// qu'il peut valider (limite de son rôle, avec <c>pos.approve_discount</c>).</summary>
public sealed record DiscountLimits(decimal? SelfLimitPercent, decimal? ApprovalLimitPercent);

public interface IPosLimits
{
    Task<DiscountLimits> DiscountLimitsAsync(CancellationToken ct = default);
}

/// <summary>
/// Expansion d'un produit composé (plat, kit, produit fini) vers la consommation de ses
/// ingrédients au moment de la vente (Phase 8 §6, §22-24). Ne crée aucun système de stock
/// parallèle : renvoie des <see cref="Nexora.Application.Inventory.StockMovementRequest"/>
/// que le noyau applique via <c>IStockLedger</c>.
/// </summary>
public interface IRecipeExpansion
{
    /// <param name="dishQuantity">Quantité de plats vendue.</param>
    /// <returns><c>null</c> si le produit n'a pas de recette ; sinon le coût unitaire (ingrédients)
    /// et les mouvements de sortie de stock des ingrédients.</returns>
    Task<RecipeExpansion?> ExpandAsync(Guid productId, decimal dishQuantity, Guid inventoryLocationId, CancellationToken ct = default);
}

public sealed record RecipeExpansion(decimal UnitCost, IReadOnlyList<Nexora.Application.Inventory.StockMovementRequest> IngredientMovements);

public sealed record CreditDecision(bool Allowed, bool RequiresOverride, decimal AvailableCredit, decimal ProjectedExposure, string? Message);

/// <summary>Contrôle du crédit client (Phase 6 §17).</summary>
public interface ICreditControl
{
    Task<CreditDecision> EvaluateAsync(Guid customerId, string currency, decimal newSaleAmount, CancellationToken ct = default);

    /// <summary>Échéance par défaut d'un crédit (module clients §6, §13) : durée de la fiche client,
    /// sinon réglage <c>credit.default_due_days</c> (30 jours par défaut).</summary>
    Task<DateOnly> DefaultDueDateAsync(Guid customerId, DateOnly from, CancellationToken ct = default);

    /// <summary>Module clients §12 : dépassement approuvé par le workflow configuré pour CE panier, CE client
    /// et au moins ce montant — non encore consommé et de moins de 2 h.</summary>
    Task<ApprovedCreditOverride?> FindApprovedOverrideAsync(Guid cartOperationUuid, Guid customerId, decimal amount, CancellationToken ct = default);
    /// <summary>Marque l'approbation comme utilisée par la vente (dans la transaction de la vente).</summary>
    Task ConsumeOverrideAsync(Guid approvalRequestId, Guid saleId, CancellationToken ct = default);
}

public sealed record SaleReceivableBalance(Guid ReceivableId, decimal Remaining);

/// <summary>Dépassement approuvé : la demande et son dernier approbateur (tracé sur la vente).</summary>
public sealed record ApprovedCreditOverride(Guid RequestId, Guid? ApprovedBy, string Reason);

// --- Stores -------------------------------------------------------------

/// <summary>Moyens d'encaissement de la caisse et comptes compatibles (phase 5).</summary>
public interface ITenderOptionsQuery
{
    Task<IReadOnlyList<Nexora.Contracts.Sales.TenderMethodOptionDto>> ListAsync(string currency, CancellationToken ct = default);
}

public interface ISalesStore
{
    Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default);
    Task<string> NextNumberAsync(string documentType, CancellationToken ct = default);
    Task<Guid?> FindSaleByOperationAsync(Guid operationUuid, CancellationToken ct = default);
    Task<Domain.Sales.Sale?> GetSaleWithLinesAsync(Guid saleId, CancellationToken ct = default);
    Task AddSaleAsync(Domain.Sales.Sale sale, CancellationToken ct = default);
    Task AddPaymentAsync(Domain.Finance.Payment payment, CancellationToken ct = default);
    Task AddSalePaymentAsync(Domain.Sales.SalePayment link, CancellationToken ct = default);
    Task AddSalePromotionAsync(Domain.Sales.SalePromotion promo, CancellationToken ct = default);
    /// <summary>Incrémente l'usage d'un coupon s'il reste sous sa limite ; <c>false</c> = coupon épuisé.</summary>
    Task<bool> ConsumeCouponAsync(Guid couponId, CancellationToken ct = default);
    Task<Guid?> DefaultTaxIdAsync(Guid productId, CancellationToken ct = default);
    Task<bool> ProductRequiresStockAsync(Guid productId, CancellationToken ct = default);
    Task<bool> ProductTracksLotsAsync(Guid productId, CancellationToken ct = default);
    Task<string> ProductNameAsync(Guid productId, CancellationToken ct = default);
    Task MarkSaleStatusAsync(Guid saleId, string status, CancellationToken ct = default);
    Task<Guid?> SessionCashAccountAsync(Guid sessionId, CancellationToken ct = default);
    /// <summary>Comptes d'encaissement de l'entreprise parmi <paramref name="ids"/> (phase 5).</summary>
    Task<IReadOnlyList<TenderAccountInfo>> TenderAccountsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    /// <summary>Moyens de paiement de l'entreprise parmi <paramref name="ids"/> (phase 5).</summary>
    Task<IReadOnlyList<TenderMethodInfo>> TenderMethodsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    /// <summary>Succursale de l'emplacement de stock (résumé fonctionnel §4 : chaque vente appartient à une succursale).</summary>
    Task<Guid?> LocationEstablishmentAsync(Guid inventoryLocationId, CancellationToken ct = default);
}

public interface ISalesReturnStore
{
    Task<string> NextNumberAsync(string documentType, CancellationToken ct = default);
    Task<Guid?> FindReturnByOperationAsync(Guid operationUuid, CancellationToken ct = default);
    Task AddReturnAsync(Domain.Sales.SaleReturn ret, CancellationToken ct = default);
    Task<Domain.Sales.Sale?> GetSaleWithLinesAsync(Guid saleId, CancellationToken ct = default);
    Task<Guid?> ReceivableForSaleAsync(Guid saleId, CancellationToken ct = default);
    /// <summary>Crédit né de la vente et son reste dû (0 si soldé, radié ou annulé) — module clients §11.</summary>
    Task<SaleReceivableBalance?> ReceivableBalanceForSaleAsync(Guid saleId, CancellationToken ct = default);
    /// <summary>Quantités déjà retournées (retours exécutés) par ligne de vente — guide §10.2.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> ReturnedQuantitiesAsync(Guid saleId, CancellationToken ct = default);
    Task SetSaleStatusAsync(Guid saleId, string status, CancellationToken ct = default);
    Task<string> CurrencyCodeAsync(Guid currencyId, CancellationToken ct = default);
}

public interface ICashSessionStore
{
    Task<string> NextNumberAsync(string documentType, CancellationToken ct = default);
    Task<Domain.Sales.CashRegister?> GetRegisterAsync(Guid registerId, CancellationToken ct = default);
    Task<Domain.Sales.CashSession?> GetOpenSessionAsync(Guid registerId, CancellationToken ct = default);
    Task<Domain.Sales.CashSession?> GetSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task AddSessionAsync(Domain.Sales.CashSession session, CancellationToken ct = default);
    Task AddMovementAsync(Domain.Sales.CashMovement movement, CancellationToken ct = default);
    Task<(int SaleCount, decimal CashIn, decimal CashOut)> SessionTotalsAsync(Guid sessionId, CancellationToken ct = default);
    /// <summary>Clôture comptée en attente de validation Manager (statut <c>closing</c>, guide §13).</summary>
    Task MarkCountedAsync(Guid sessionId, decimal expected, decimal counted, string? notes, CancellationToken ct = default);
    /// <summary>Clôture définitive ; <paramref name="validatedBy"/> renseigné quand un Manager a validé l'écart.</summary>
    Task FinalizeCloseAsync(Guid sessionId, decimal expected, decimal counted, string? notes,
        Guid? validatedBy, string? validationNote, CancellationToken ct = default);
    Task<string> CurrencyCodeAsync(Guid currencyId, CancellationToken ct = default);
}

internal static class Rounding
{
    public static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);
}
