using Nexora.Contracts.Finance;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance;

/// <summary>
/// Gestion des taux de change (audit 2026-10, A-017) : la table était lue partout (ventes et comptes en devise,
/// consolidation, contrôle de crédit) mais aucune fonction ne permettait d'y écrire — le multidevise ne marchait
/// qu'avec les deux taux du jeu de démonstration.
/// </summary>
public interface IExchangeRateService
{
    Task<IReadOnlyList<ExchangeRateDto>> ListAsync(string? baseCurrency, string? quoteCurrency, CancellationToken ct = default);
    Task<Result<ExchangeRateDto>> RecordAsync(RecordExchangeRateRequest request, CancellationToken ct = default);
}
