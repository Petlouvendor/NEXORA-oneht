using Nexora.Contracts.Finance;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance.Treasury;

/// <summary>
/// Lectures de pilotage de trésorerie (Phase 28 §26-28, §22-23). Dérivées des comptes
/// financiers, des sous-livres créances/dettes et des taux de change — jamais de saisie
/// parallèle (§1). La comptabilité reste le système de référence pour les états financiers.
/// </summary>
public interface ITreasuryQueries
{
    /// <summary>Comptes financiers de l'entreprise avec solde système et solde converti (§3-4, §50).</summary>
    Task<IReadOnlyList<TreasuryAccountLineDto>> AccountsAsync(CancellationToken ct = default);

    /// <summary>Position de trésorerie : disponible par nature de compte, flux proches, net (§27).</summary>
    Task<TreasuryPositionDto> PositionAsync(CancellationToken ct = default);

    /// <summary>Liquidité immédiate, encaissements/décaissements attendus, gap et alertes (§28).</summary>
    Task<LiquidityDto> LiquidityAsync(int? horizonDays, CancellationToken ct = default);

    /// <summary>Prévision par tranches de maturité : aujourd'hui / 7 / 30 / 60 / 90 j (§22-23).</summary>
    Task<CashflowForecastDto> MaturityForecastAsync(CancellationToken ct = default);
}

/// <summary>
/// File d'attente des sommes non identifiées / non affectées (Phase 28 §31-32). Le montant
/// lui-même est déjà comptabilisé sur un compte d'attente par l'événement d'origine ; cet
/// élément est la tâche de résolution associée, tracée et auditée.
/// </summary>
public interface ISuspenseService
{
    Task<IReadOnlyList<SuspenseItemDto>> ListAsync(string? status, bool onlyOverdue, CancellationToken ct = default);
    Task<Result<Guid>> OpenAsync(OpenSuspenseRequest r, CancellationToken ct = default);
    Task<Result> ResolveAsync(Guid id, ResolveSuspenseRequest r, CancellationToken ct = default);
}

/// <summary>
/// Clôture financière d'une période (Phase 28 §38) : exécute les contrôles (comptes rapprochés,
/// sessions ouvertes, suspens, écarts, créances/dettes), produit un rapport horodaté et
/// enregistre la clôture. N'écrit aucune écriture comptable — la clôture comptable (Phase 4 §27)
/// reste distincte.
/// </summary>
public interface IFinancialCloseService
{
    Task<Result<FinancialCloseReportDto>> PreviewAsync(Guid periodId, CancellationToken ct = default);
    Task<Result<FinancialCloseResultDto>> CompleteAsync(Guid periodId, CompleteFinancialCloseRequest r, CancellationToken ct = default);
}
