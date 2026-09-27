using Nexora.Contracts.Sales;
using Nexora.Domain.Common;

namespace Nexora.Application.Sales;

/// <summary>
/// Retours soumis à validation Manager (guide fonctionnel §10). Le caissier (<c>pos.return</c>)
/// demande ; un approbateur distinct (<c>pos.refund</c>) approuve — ce qui exécute le retour réel
/// (stock, remboursement, écriture) dans la même transaction — ou refuse avec un motif. La demande
/// reste dans l'historique dans tous les cas (§10.2) et chaque transition est auditée (§22).
/// </summary>
public interface IReturnRequestService
{
    /// <summary>Vente identifiée par son numéro de ticket (§10.1) — <c>null</c> si introuvable.</summary>
    Task<ReturnableSaleDto?> FindSaleForReturnAsync(string documentNumber, CancellationToken ct = default);
    Task<Result<ReturnRequestDto>> RequestAsync(CreateReturnRequest request, CancellationToken ct = default);
    Task<Result<ReturnRequestDto>> ApproveAsync(Guid requestId, DecideRequest decision, CancellationToken ct = default);
    Task<Result<ReturnRequestDto>> RejectAsync(Guid requestId, DecideRequest decision, CancellationToken ct = default);
    /// <summary>Un approbateur voit toutes les demandes de l'entreprise ; un caissier, les siennes.</summary>
    Task<IReadOnlyList<ReturnRequestDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<Result<ReturnRequestDto>> GetAsync(Guid requestId, CancellationToken ct = default);
}

/// <summary>
/// Remises exceptionnelles (guide §12) : au-delà de sa limite, le vendeur (<c>pos.discount</c>)
/// demande ; un Manager (<c>pos.approve_discount</c>), distinct et dans SA limite d'approbation,
/// approuve ou refuse. L'approbation vaut pour ce panier, ce produit et ce vendeur, deux heures, et
/// est consommée par la vente. Chaque demande et décision est auditée (§22).
/// </summary>
public interface IDiscountRequestService
{
    Task<Result<DiscountRequestDto>> RequestAsync(CreateDiscountRequest request, CancellationToken ct = default);
    Task<Result<DiscountRequestDto>> ApproveAsync(Guid requestId, DecideRequest decision, CancellationToken ct = default);
    Task<Result<DiscountRequestDto>> RejectAsync(Guid requestId, DecideRequest decision, CancellationToken ct = default);
    /// <summary>Un approbateur voit toutes les demandes ; un vendeur, les siennes.</summary>
    Task<IReadOnlyList<DiscountRequestDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<Result<DiscountRequestDto>> GetAsync(Guid requestId, CancellationToken ct = default);
    /// <summary>Limites et droits POS de l'utilisateur courant, pour adapter l'écran.</summary>
    Task<PosLimitsDto> MyLimitsAsync(CancellationToken ct = default);
}

/// <summary>Notifications des validations Manager (guide §20) : aux détenteurs d'une permission
/// d'approbation dans l'entreprise, puis au demandeur. Ne fait jamais échouer l'opération métier.</summary>
public interface IPosApprovalNotifier
{
    Task NotifyApproversAsync(string permission, string kind, string number, string summary,
        string actionUrl, string referenceType, Guid referenceId, CancellationToken ct);
    Task NotifyRequesterAsync(Guid requester, string kind, string number, bool approved, string? note,
        string actionUrl, string referenceType, Guid referenceId, CancellationToken ct);
}

/// <summary>Lectures des sessions de caisse pour l'écran POS (guide §13) : la session de
/// l'utilisateur, les caisses ouvrables, et — pour un superviseur — les clôtures à valider.</summary>
public interface ICashSessionQueries
{
    Task<CurrentCashSessionDto?> CurrentAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CashRegisterDto>> RegistersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PendingClosingDto>> PendingClosingsAsync(CancellationToken ct = default);
    /// <summary>Sessions ouvertes des AUTRES utilisateurs — superviseur (<c>pos.manage_sessions</c>) uniquement :
    /// une caisse oubliée ouverte bloque les autres caissiers ; le responsable la clôture.</summary>
    Task<IReadOnlyList<OpenCashSessionDto>> OpenSessionsAsync(CancellationToken ct = default);
}

/// <summary>
/// Recherche produit du POS (guide §9, §24) : après quelques caractères, une liste courte (5-10,
/// configurable) triée par pertinence, limitée aux produits actifs et vendables, sans coût ni
/// fournisseur. Distincte de la recherche catalogue de gestion (<c>catalog.view</c>).
/// </summary>
public interface IPosCatalogQueries
{
    Task<PosProductSearchDto> SearchAsync(string term, Guid? inventoryLocationId, CancellationToken ct = default);
    /// <summary>Code-barres, PLU ou SKU exact — <c>null</c> si aucun produit vendable ne correspond.</summary>
    Task<PosProductDto?> ScanAsync(string code, Guid? inventoryLocationId, CancellationToken ct = default);
}

/// <summary>
/// Validation sur place (guide §10, §12, §13) : quand le responsable n'est pas devant son ordinateur,
/// il valide au poste du caissier avec SES identifiants. Mêmes protections que la connexion
/// (verrouillage après échecs, MFA, journal de sécurité), permission exigée selon l'action, jamais le
/// demandeur lui-même ; l'action est exécutée et auditée au nom du responsable, et le caissier ne
/// reçoit aucun droit supplémentaire (aucun jeton n'est émis).
/// </summary>
public interface IManagerOverrideService
{
    Task<Result<ManagerOverrideResultDto>> ExecuteAsync(ManagerOverrideRequest request, string? ipAddress, CancellationToken ct = default);
}

