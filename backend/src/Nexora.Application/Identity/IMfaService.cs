using Nexora.Contracts.Auth;
using Nexora.Domain.Common;

namespace Nexora.Application.Identity;

/// <summary>
/// Authentification multi-facteur TOTP (Phase 33 §8-9). Couvre l'enrôlement self-service,
/// la confirmation, la désactivation, la réinitialisation par un administrateur, et les
/// primitives de défi utilisées par la connexion (émettre / vérifier un défi). Le secret
/// TOTP et les codes de récupération ne sont jamais renvoyés en clair après leur création.
/// </summary>
public interface IMfaService
{
    /// <summary>Statut MFA de l'utilisateur courant (activée ? nb de codes de récupération restants ? imposée par la politique ?).</summary>
    Task<MfaStatusDto> StatusAsync(CancellationToken ct = default);

    /// <summary>Vrai si au moins un rôle actif de l'utilisateur figure dans <c>security_policy.mfa_required_roles</c> (§37).</summary>
    Task<bool> PolicyRequiresMfaAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Démarre l'enrôlement : génère un secret et l'URI <c>otpauth://</c> (non encore actif).</summary>
    Task<Result<MfaEnrollmentDto>> BeginEnrollmentAsync(CancellationToken ct = default);

    /// <summary>Confirme l'enrôlement avec un premier code valide → active la MFA et renvoie les codes de récupération (une seule fois).</summary>
    Task<Result<MfaRecoveryCodesDto>> ConfirmEnrollmentAsync(MfaConfirmRequest request, CancellationToken ct = default);

    /// <summary>Désactive la MFA de l'utilisateur courant (exige un code TOTP ou de récupération valide).</summary>
    Task<Result> DisableAsync(MfaDisableRequest request, CancellationToken ct = default);

    /// <summary>Réinitialise la MFA d'un autre utilisateur autorisé (permission <c>security.reset_mfa</c>, §9, §39).</summary>
    Task<Result> ResetForUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Crée un défi de connexion court après vérification du mot de passe (retourne le jeton en clair).</summary>
    Task<(string Token, DateTimeOffset ExpiresAt)> IssueChallengeAsync(Guid userId, Guid? companyId, CancellationToken ct = default);

    /// <summary>Consomme un défi et vérifie le code TOTP / de récupération. Retourne l'utilisateur validé et l'entreprise demandée.</summary>
    Task<Result<MfaLoginTicket>> VerifyChallengeAsync(string token, string code, CancellationToken ct = default);
}

/// <summary>Résultat d'un défi MFA réussi : de quoi émettre le jeton d'accès final.</summary>
public sealed record MfaLoginTicket(Guid UserId, Guid? CompanyId);
