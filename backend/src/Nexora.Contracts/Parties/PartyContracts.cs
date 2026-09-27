namespace Nexora.Contracts.Parties;

// =============================================================================
// Tiers — clients & fournisseurs (recette Phase 55 : CLI-001/002, ACH-001/011).
// Les tables `customer` / `supplier` existent depuis la Phase 2 et les permissions
// `customers.*` / `suppliers.*` depuis la V021, mais aucune API ne permettait de créer
// ou modifier un tiers en dehors du pipeline d'import (Phase 39).
// =============================================================================

/// <summary><c>CreditLimit</c> / <c>OpenReceivables</c> = <c>null</c> quand l'appelant ne peut pas les voir
/// (guide §7.2, §16) : liste → <c>credit.view_portfolio</c> ; fiche d'un client → en plus
/// <c>pos.view_customer_balance</c> (solde utile à la vente à crédit).
/// Module clients §6 : fiche complète (identité, contact, commercial, crédit, métadonnées).</summary>
public sealed record CustomerDto(
    Guid Id, string CustomerNumber, string Name, string Type, string? Phone, string? Email, string? Address,
    string? TaxIdentifier, string Status, decimal? CreditLimit, string Currency, decimal? OpenReceivables,
    DateTimeOffset CreatedAt,
    string? FirstName = null, string? City = null, string? Country = null,
    string? SecondaryContactName = null, string? SecondaryContactPhone = null,
    string? Segment = null, Guid? CustomerGroupId = null, string? CustomerGroupName = null,
    Guid? AccountManagerId = null, string? AccountManagerName = null,
    int? CreditDays = null, string? CreditTerms = null,
    Guid? EstablishmentId = null, string? EstablishmentName = null, string? Notes = null,
    string? CreatedByName = null, DateTimeOffset? UpdatedAt = null);

/// <summary><c>CreditLimit</c> &gt; 0 exige <c>credit.set_limit</c> — un caissier peut créer un
/// client, jamais lui ouvrir du crédit (CLI-007). Durée / conditions de crédit : idem.</summary>
public sealed record CreateCustomerRequest(
    string Name, string Type = "individual", string? Phone = null, string? Email = null, string? Address = null,
    string? TaxIdentifier = null, decimal CreditLimit = 0, string? Currency = null, string? CustomerNumber = null,
    string? FirstName = null, string? City = null, string? Country = null,
    string? SecondaryContactName = null, string? SecondaryContactPhone = null,
    string? Segment = null, Guid? CustomerGroupId = null, Guid? AccountManagerId = null,
    int? CreditDays = null, string? CreditTerms = null, Guid? EstablishmentId = null, string? Notes = null);

/// <summary>Remplacement complet (PUT). Les champs de crédit (<c>CreditLimit</c>, <c>CreditDays</c>,
/// <c>CreditTerms</c>) à <c>null</c> restent inchangés ; les modifier exige <c>credit.set_limit</c>.</summary>
public sealed record UpdateCustomerRequest(
    string Name, string Type = "individual", string? Phone = null, string? Email = null, string? Address = null,
    string? TaxIdentifier = null, decimal? CreditLimit = null,
    string? FirstName = null, string? City = null, string? Country = null,
    string? SecondaryContactName = null, string? SecondaryContactPhone = null,
    string? Segment = null, Guid? CustomerGroupId = null, Guid? AccountManagerId = null,
    int? CreditDays = null, string? CreditTerms = null, Guid? EstablishmentId = null, string? Notes = null);

/// <summary>Modification partielle (PATCH, §22) : un champ absent (<c>null</c>) reste inchangé.</summary>
public sealed record PatchCustomerRequest(
    string? Name = null, string? Type = null, string? Phone = null, string? Email = null, string? Address = null,
    string? TaxIdentifier = null, decimal? CreditLimit = null,
    string? FirstName = null, string? City = null, string? Country = null,
    string? SecondaryContactName = null, string? SecondaryContactPhone = null,
    string? Segment = null, Guid? CustomerGroupId = null, Guid? AccountManagerId = null,
    int? CreditDays = null, string? CreditTerms = null, Guid? EstablishmentId = null, string? Notes = null);

/// <summary>Statut de fiche (§29) : active / inactive / archived. « blocked » passe par les blocages
/// (<c>/credit/blocks</c>, Phase 27) qui gardent motif et historique.</summary>
public sealed record ChangeCustomerStatusRequest(string Status, string Reason);

public sealed record SupplierDto(
    Guid Id, string SupplierNumber, string Name, string Type, string? Phone, string? Email, string? Address,
    string? TaxIdentifier, string Status, string Currency, decimal OpenPayables, DateTimeOffset CreatedAt);

public sealed record CreateSupplierRequest(
    string Name, string Type = "business", string? Phone = null, string? Email = null, string? Address = null,
    string? TaxIdentifier = null, string? Currency = null, string? SupplierNumber = null);

public sealed record UpdateSupplierRequest(
    string Name, string Type = "business", string? Phone = null, string? Email = null, string? Address = null,
    string? TaxIdentifier = null);
