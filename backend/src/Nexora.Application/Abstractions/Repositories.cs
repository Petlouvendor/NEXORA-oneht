using Nexora.Domain.Identity;
using Nexora.Domain.Organization;

namespace Nexora.Application.Abstractions;

public interface IUserRepository
{
    Task<AppUser?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<AppUser?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task TouchLastLoginAsync(Guid id, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>
    /// Enregistre un échec de connexion (Phase 33 §7, §48) : incrémente le compteur et, au seuil,
    /// verrouille temporairement le compte. Retourne l'instant de déverrouillage si le compte est verrouillé.
    /// </summary>
    Task<DateTimeOffset?> RegisterFailedLoginAsync(Guid id, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Remet à zéro le compteur d'échecs et lève le verrouillage (connexion réussie).</summary>
    Task ResetFailedLoginsAsync(Guid id, CancellationToken ct = default);
}

public interface ICompanyRepository
{
    Task<IReadOnlyList<Company>> ListForCurrentScopeAsync(CancellationToken ct = default);
    Task<Company?> GetAsync(Guid id, CancellationToken ct = default);
}
