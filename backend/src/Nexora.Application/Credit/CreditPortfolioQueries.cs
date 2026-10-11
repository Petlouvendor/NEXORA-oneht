using Nexora.Contracts.Credit;

namespace Nexora.Application.Credit;

/// <summary>Lectures crédit & recouvrement : portefeuille, aging, relevé (Phase 27 §15, §23, §29, §35).</summary>
public interface ICreditPortfolioQueries
{
    Task<IReadOnlyList<CollectionsRowDto>> CollectionsAsync(bool onlyOverdue, CancellationToken ct = default);
    Task<AgingSummaryDto> AgingSummaryAsync(CancellationToken ct = default);
    Task<CustomerStatementDto?> CustomerStatementAsync(Guid customerId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<IReadOnlyList<PromiseDto>> PromisesAsync(Guid? customerId, string? status, CancellationToken ct = default);
    Task<IReadOnlyList<DisputeDto>> DisputesAsync(Guid? customerId, string? status, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerBlockDto>> BlocksAsync(Guid? customerId, bool onlyActive, CancellationToken ct = default);
    /// <summary>Créances d'un client (ouvertes, ou toutes) avec promesse / litige en cours.</summary>
    Task<IReadOnlyList<CreditReceivableDto>> ReceivablesAsync(Guid customerId, bool onlyOpen, CancellationToken ct = default);
    Task<IReadOnlyList<WriteOffDto>> WriteOffsAsync(Guid? customerId, CancellationToken ct = default);
    /// <summary>Agenda : relances prévues échues (dernière action du client seulement), promesses à échéance,
    /// promesses non tenues sans action depuis.</summary>
    Task<IReadOnlyList<FollowUpDto>> FollowUpsAsync(CancellationToken ct = default);
}
