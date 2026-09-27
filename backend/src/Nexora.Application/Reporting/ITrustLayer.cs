using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;

namespace Nexora.Application.Reporting;

/// <summary>
/// Réconciliation KPI (Phase 31 §46) : prouve que les chiffres du reporting correspondent
/// aux sources métier / comptables — CA reporting = ventes, encaissements = paiements confirmés,
/// stock = ledger, créances = comptes clients, dettes = comptes fournisseurs.
/// </summary>
public interface IKpiReconciliation
{
    Task<ReconciliationReportDto> ReconcileAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);
}

/// <summary>
/// Contrôle qualité des données (Phase 31 §45) : écritures non validées, lignes bancaires
/// non rapprochées, suspens ouverts, produits sans coût / prix, stocks négatifs, mappings
/// comptables manquants… + score global.
/// </summary>
public interface IDataQualityService
{
    Task<DataQualityReportDto> EvaluateAsync(CancellationToken ct = default);
}

/// <summary>
/// Photos analytiques reproductibles (Phase 31 §44) : fige le résultat d'une réconciliation
/// ou d'un contrôle qualité (périmètre + version de calcul + jsonb) pour rejeu à l'identique.
/// </summary>
public interface IAnalyticsSnapshotService
{
    Task<Result<AnalyticsSnapshotDto>> CreateAsync(CreateSnapshotRequest r, CancellationToken ct = default);
    Task<IReadOnlyList<AnalyticsSnapshotDto>> ListAsync(string? kind, CancellationToken ct = default);
    Task<Result<AnalyticsSnapshotDetailDto>> GetAsync(Guid id, CancellationToken ct = default);
}
