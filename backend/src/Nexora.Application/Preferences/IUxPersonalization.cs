using Nexora.Contracts.Preferences;
using Nexora.Domain.Common;

namespace Nexora.Application.Preferences;

// =============================================================================
// Personnalisation UX persistée (Phase 40 §13, §21-22, §37, §50).
// Tout est en libre-service (RLS `user_id`) ; poser un défaut d'entreprise/rôle
// exige `admin.configure_company`.
// =============================================================================

/// <summary>Dispositions de tableau de bord — personnelles ou défauts d'entreprise/rôle (§13, §37).</summary>
public interface IDashboardLayoutService
{
    /// <summary>Ma disposition effective : ma disposition perso, sinon le défaut de mon rôle, sinon le défaut d'entreprise.</summary>
    Task<DashboardLayoutDto?> GetEffectiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DashboardLayoutDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Enregistre / met à jour MA disposition (libre-service).</summary>
    Task<Result<DashboardLayoutDto>> SaveAsync(SaveDashboardLayoutRequest request, CancellationToken ct = default);

    /// <summary>Enregistre un défaut d'entreprise (éventuellement ciblé par rôle) — route protégée par `admin.configure_company`.</summary>
    Task<Result<DashboardLayoutDto>> SaveCompanyDefaultAsync(SaveDashboardLayoutRequest request, CancellationToken ct = default);

    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Filtres de tableaux sauvegardés, privés ou partagés dans l'entreprise (§21-22, §44).</summary>
public interface ISavedFilterService
{
    Task<IReadOnlyList<SavedFilterDto>> ListAsync(string? viewKey, CancellationToken ct = default);
    Task<Result<SavedFilterDto>> SaveAsync(SaveFilterRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Raccourcis clavier / actions rapides de l'utilisateur (§11, §20, §37-38).</summary>
public interface IShortcutService
{
    Task<IReadOnlyList<ShortcutDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ShortcutDto>> SaveAsync(SaveShortcutRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Version &amp; catalogue du design system (§50, §57) — pour le contrôle de compatibilité front / handoff.</summary>
public interface IDesignSystemService
{
    DesignSystemVersionDto GetVersion();
}
