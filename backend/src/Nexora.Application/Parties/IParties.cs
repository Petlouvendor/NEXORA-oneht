using Nexora.Contracts.Parties;
using Nexora.Domain.Common;

namespace Nexora.Application.Parties;

/// <summary>Fiche client (recette Phase 55 CLI-001/002). Numéro attribué automatiquement
/// (<c>CLI-00001</c>…) s'il n'est pas fourni, unique par entreprise.</summary>
public interface ICustomerService
{
    Task<IReadOnlyList<CustomerDto>> ListAsync(string? q, string? status, int limit = 100, int offset = 0, CancellationToken ct = default);
    Task<CustomerDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<CustomerDto>> CreateAsync(CreateCustomerRequest request, CancellationToken ct = default);
    Task<Result<CustomerDto>> UpdateAsync(Guid id, UpdateCustomerRequest request, CancellationToken ct = default);
    /// <summary>Modification partielle (module clients §22 PATCH) : un champ absent reste inchangé.</summary>
    Task<Result<CustomerDto>> PatchAsync(Guid id, PatchCustomerRequest request, CancellationToken ct = default);
    /// <summary>Actif / inactif / archivé (§29) — archivage refusé tant que le compte n'est pas soldé.</summary>
    Task<Result<CustomerDto>> ChangeStatusAsync(Guid id, ChangeCustomerStatusRequest request, CancellationToken ct = default);
}

/// <summary>Fiche fournisseur (recette Phase 55 ACH-001/011). Numéro <c>FRN-00001</c>… si absent.</summary>
public interface ISupplierService
{
    Task<SupplierDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<SupplierDto>> CreateAsync(CreateSupplierRequest request, CancellationToken ct = default);
    Task<Result<SupplierDto>> UpdateAsync(Guid id, UpdateSupplierRequest request, CancellationToken ct = default);
}
