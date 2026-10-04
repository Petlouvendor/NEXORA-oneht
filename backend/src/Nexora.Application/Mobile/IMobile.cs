using Nexora.Contracts.Mobile;
using Nexora.Domain.Common;

namespace Nexora.Application.Mobile;

/// <summary>
/// Pièces jointes minimales (Phase 44 §22-23) — photo/signature uniquement, aucun versioning ni
/// recherche (Phase 45, GED, généralisera si besoin). Le contenu est stocké tel quel (≤ 5 Mo,
/// capture mobile) ; l'accès reste soumis aux mêmes permissions que la ressource référencée.
/// </summary>
public interface IAttachmentService
{
    Task<Result<AttachmentDto>> UploadAsync(UploadAttachmentRequest request, CancellationToken ct = default);
    Task<Result<(byte[] Content, string ContentType)>> GetContentAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Tournées de livraison (Phase 44 §12, §52). La complétion d'une étape liée à une commande
/// réutilise <see cref="Sales.OrderService.CompleteOrderAsync"/> (Phase 25) — aucune vente ne se
/// crée deux fois, le statut de la commande découle du même appel qu'en caisse.
/// </summary>
public interface IDeliveryService
{
    Task<Result<DeliveryRouteDto>> CreateRouteAsync(CreateDeliveryRouteRequest request, CancellationToken ct = default);
    /// <summary>Options de saisie terrain (agents habilités, commandes livrables, caisses, géolocalisation prévue ou non).</summary>
    Task<FieldOptionsDto> OptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryRouteDto>> ListRoutesAsync(DateOnly? date, CancellationToken ct = default);
    Task<Result<DeliveryRouteDto>> GetRouteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Étapes assignées à l'appelant (§10-12) — jamais une extraction complète des tournées.</summary>
    Task<IReadOnlyList<DeliveryStopDto>> MyStopsAsync(CancellationToken ct = default);

    /// <summary><c>status</c> ∈ en_route|arrived — transitions en ligne, non critiques hors ligne.</summary>
    Task<Result<DeliveryStopDto>> AdvanceStopAsync(Guid stopId, string status, CancellationToken ct = default);

    /// <summary>Rejouable hors ligne (§25-29, mêmes garanties que la caisse Phase 10).</summary>
    Task<Result<DeliveryStopDto>> CompleteStopAsync(Guid stopId, CompleteDeliveryStopRequest request, CancellationToken ct = default);
    Task<Result<DeliveryStopDto>> FailStopAsync(Guid stopId, FailDeliveryStopRequest request, CancellationToken ct = default);
}

/// <summary>Interventions techniciens (Phase 44 §13) — comble un manque réel (aucune fiche
/// d'intervention n'existait ; <c>support_ticket</c> Phase 38 est le support NEXORA↔abonné, sans
/// rapport). Checklist/pièces/temps/signature/rapport, comme la fiche papier qu'elle remplace.</summary>
public interface IFieldInterventionService
{
    Task<Result<FieldInterventionDto>> CreateAsync(CreateInterventionRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<FieldInterventionDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<IReadOnlyList<FieldInterventionDto>> MineAsync(CancellationToken ct = default);
    Task<Result<FieldInterventionDto>> StartAsync(Guid id, CancellationToken ct = default);

    /// <summary>Rejouable hors ligne (§25-29) — même moteur que les livraisons/ventes.</summary>
    Task<Result<FieldInterventionDto>> CompleteAsync(Guid id, CompleteInterventionRequest request, CancellationToken ct = default);
}

/// <summary>Diagnostics minimisés (Phase 44 §46) — jamais de donnée sensible inutile, un simple
/// journal d'ajout (comme <c>assistant_query</c>, Phase 43).</summary>
public interface ICrashReportService
{
    Task<Result<CrashReportDto>> ReportAsync(ReportCrashRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CrashReportDto>> ListAsync(int limit, CancellationToken ct = default);
}

/// <summary>
/// Configuration & bootstrap mobile (Phase 44 §25, §48) — <see cref="ConfigAsync"/> agrège la
/// politique offline existante (Phase 10) sans la redéfinir ; <see cref="PullAsync"/> renvoie le
/// périmètre « données autorisées en cache » strictement propre à l'appelant (§25).
/// </summary>
public interface IMobileBootstrapService
{
    Task<MobileConfigDto> ConfigAsync(CancellationToken ct = default);
    Task<MobileBootstrapDto> PullAsync(CancellationToken ct = default);
}
