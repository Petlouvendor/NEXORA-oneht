using Nexora.Contracts.Finance;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance.Ledger;

/// <summary>
/// Plan comptable (finalisation produit, phase 8). Un compte mouvementé garde son code, son type et sa nature
/// (mouvementable ou de regroupement) : sinon la balance et les états déjà produits changeraient de sens. Un compte
/// ne se désactive pas s'il porte un solde ou s'il sert aux écritures automatiques.
/// </summary>
public interface IChartManagementService
{
    Task<IReadOnlyList<ChartAccountDto>> AccountsAsync(CancellationToken ct = default);
    /// <summary>Devises saisissables en comptabilité (devise de l'entreprise en premier).</summary>
    Task<IReadOnlyList<FinanceCurrencyOptionDto>> CurrenciesAsync(CancellationToken ct = default);
    Task<Result<Guid>> CreateAsync(SaveChartAccountRequest request, CancellationToken ct = default);
    Task<Result> UpdateAsync(Guid id, SaveChartAccountRequest request, CancellationToken ct = default);
    Task<Result> SetStatusAsync(Guid id, ChartAccountStatusRequest request, CancellationToken ct = default);
}
