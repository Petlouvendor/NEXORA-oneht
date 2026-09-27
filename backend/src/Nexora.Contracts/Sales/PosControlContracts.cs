namespace Nexora.Contracts.Sales;

// =============================================================================
// Contrôles POS du guide fonctionnel développeur (docs/guide-fonctionnel-conformite.md) :
// demandes de retour validées par un Manager (§10), recherche POS bornée (§9, §24),
// remises exceptionnelles (§12), sessions de caisse (§13).
// Aucun de ces contrats ne porte de coût, de marge ou de stock chiffré (§7.2).
// =============================================================================

// --- Retours (§10) --------------------------------------------------------

/// <summary>Vente retrouvée par son numéro de ticket, réduite à ce qu'il faut pour identifier les
/// articles à retourner (§10.1 étape 2) : quantités vendues / déjà retournées / en attente.</summary>
public sealed record ReturnableSaleDto(
    Guid SaleId, string DocumentNumber, DateTimeOffset SoldAt, string Status, string Currency, decimal Total,
    IReadOnlyList<ReturnableSaleLineDto> Lines);

public sealed record ReturnableSaleLineDto(
    Guid SaleLineId, Guid ProductId, string Description, decimal QuantitySold, decimal QuantityReturned,
    decimal QuantityPending, decimal QuantityReturnable, decimal UnitPrice, decimal LineTotal);

public sealed record ReturnRequestLineInput(Guid SaleLineId, decimal Quantity, bool Restock = true);

/// <summary>Demande de retour du caissier. Aucun montant : le remboursement est calculé par le serveur.</summary>
public sealed record CreateReturnRequest(
    Guid OperationUuid, Guid SaleId, Guid InventoryLocationId, Guid? CashSessionId,
    string Reason, string RefundMethod, Guid? RefundFinancialAccountId,
    IReadOnlyList<ReturnRequestLineInput> Lines);

public sealed record ReturnRequestLineDto(
    Guid SaleLineId, string Description, decimal Quantity, bool Restock, decimal EstimatedRefund);

/// <summary>Demande de retour — états <c>pending_approval → executed | rejected</c> (§26).</summary>
public sealed record ReturnRequestDto(
    Guid Id, string RequestNumber, Guid SaleId, string SaleNumber, string Status, string Reason,
    string RefundMethod, decimal EstimatedRefund, string Currency,
    Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt,
    Guid? DecidedBy, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote,
    Guid? SaleReturnId, IReadOnlyList<ReturnRequestLineDto> Lines);

/// <summary>Décision Manager. La note est obligatoire pour un refus.</summary>
public sealed record DecideRequest(string? Note);

// --- Remises exceptionnelles (§12) ---------------------------------------

/// <summary>Demande de remise au-delà de la limite du vendeur, pour une ligne du panier en cours
/// (<paramref name="OperationUuid"/> = UUID d'opération qui sera envoyé à l'encaissement). Le montant
/// brut de la ligne est recalculé par le serveur.</summary>
public sealed record CreateDiscountRequest(
    Guid OperationUuid, Guid ProductId, decimal Quantity, decimal RequestedDiscount, string Currency,
    Guid InventoryLocationId, Guid? CustomerId, string Reason);

/// <summary>États : <c>pending_approval → approved → consumed</c>, ou <c>rejected</c> / <c>expired</c>.</summary>
public sealed record DiscountRequestDto(
    Guid Id, string RequestNumber, Guid OperationUuid, Guid ProductId, string ProductName, decimal Quantity,
    decimal LineGross, decimal RequestedAmount, decimal RequestedPercent, string Currency, string Reason, string Status,
    Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt,
    Guid? DecidedBy, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote, Guid? ConsumedSaleId);

/// <summary>Ce que l'écran POS doit savoir des droits de l'utilisateur (l'API reste l'autorité).
/// Limites en % ; <c>null</c> = pas de limite.</summary>
public sealed record PosLimitsDto(
    decimal? DiscountLimitPercent, decimal? DiscountApprovalLimitPercent,
    bool CanDiscount, bool CanChangePrice, bool CanSellCredit, bool CanOverrideCredit,
    bool CanRequestReturn, bool CanRefund, bool CanApproveDiscount, bool CanManageSessions);

// --- Sessions de caisse (§13) ---------------------------------------------

/// <summary>Session de l'utilisateur. Le montant attendu n'est pas communiqué avant le comptage
/// (comptage à l'aveugle) : il apparaît dans le résultat de la clôture.</summary>
/// <paramref name="InventoryLocationId"/> = emplacement de stock du point de vente de la caisse.
public sealed record CurrentCashSessionDto(
    Guid SessionId, Guid CashRegisterId, string RegisterName, string Currency, Guid? FinancialAccountId,
    Guid? InventoryLocationId, string Status, DateTimeOffset OpenedAt, decimal OpeningBalance, int SaleCount);

/// <paramref name="OpenedBy"/> = nom de la personne dont la session occupe la caisse (aucun montant) :
/// le caissier sait à qui s'adresser, un responsable peut la clôturer (guide §13).
public sealed record CashRegisterDto(
    Guid Id, string Name, string Code, string Currency, Guid? FinancialAccountId, Guid? InventoryLocationId,
    bool HasOpenSession, string? OpenedBy);

/// <summary>Session ouverte (ou comptée) d'un caissier — vue de supervision, sans montant attendu.</summary>
public sealed record OpenCashSessionDto(
    Guid SessionId, string RegisterName, Guid CashierId, string? CashierName, string Status,
    DateTimeOffset OpenedAt, int SaleCount, string Currency);

/// <summary>Clôture comptée dont l'écart dépasse le seuil : en attente de validation Manager.</summary>
public sealed record PendingClosingDto(
    Guid SessionId, string RegisterName, Guid CashierId, string? CashierName, DateTimeOffset OpenedAt,
    DateTimeOffset? CountedAt, decimal ExpectedBalance, decimal CountedBalance, decimal Difference, string Currency, string? Notes);

// --- Recherche produit au POS (§9, §24) -----------------------------------

/// <summary>Produit proposé au POS : nom, référence, prix de vente et disponibilité SIMPLE
/// (<c>in_stock</c> | <c>low</c> | <c>out</c> | <c>untracked</c>) — jamais de quantité, de coût ni de fournisseur.</summary>
public sealed record PosProductDto(
    Guid ProductId, string Name, string Sku, string? Barcode, decimal? SalePrice, string? Currency, string Availability);

/// <summary>Liste courte et pertinente ; <paramref name="MinChars"/> / <paramref name="MaxResults"/>
/// rappellent au client les bornes configurées.</summary>
public sealed record PosProductSearchDto(IReadOnlyList<PosProductDto> Items, int MinChars, int MaxResults);

// --- Validation sur place par un responsable (§10, §12, §13) ------------------

/// <summary>
/// Le responsable, présent au poste du caissier, saisit ses identifiants dans une fenêtre dédiée.
/// <paramref name="Action"/> : <c>approve_return</c> | <c>reject_return</c> | <c>approve_discount</c> |
/// <c>reject_discount</c> | <c>validate_close</c>. <paramref name="MfaCode"/> est exigé si le
/// responsable a activé la double authentification. Le mot de passe n'est jamais journalisé ni conservé.
/// </summary>
public sealed record ManagerOverrideRequest(
    string Action, Guid TargetId, string Email, string Password, string? MfaCode, string? Note);

/// <summary>Résultat : qui a validé (le responsable, pas le caissier) et le nouvel état de l'objet.</summary>
public sealed record ManagerOverrideResultDto(
    string Action, Guid TargetId, Guid ApprovedBy, string? ApprovedByName, string Status);

