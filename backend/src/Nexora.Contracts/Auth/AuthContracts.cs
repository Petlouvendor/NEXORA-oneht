namespace Nexora.Contracts.Auth;

public sealed record LoginRequest(string Email, string Password, Guid? CompanyId);

public sealed record AuthTokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresInSeconds,
    CurrentUserResponse User)
{
    // --- Deuxième facteur (Phase 33 §8) : renseignés quand le mot de passe est correct
    //     mais qu'un code TOTP est encore requis. AccessToken est alors vide. ---

    /// <summary>Vrai si un code MFA doit être fourni via <c>POST /auth/mfa/verify</c>.</summary>
    public bool MfaRequired { get; init; }

    /// <summary>Jeton de défi court à renvoyer avec le code TOTP.</summary>
    public string? MfaToken { get; init; }

    public DateTimeOffset? MfaExpiresAt { get; init; }

    /// <summary>La politique d'entreprise impose la MFA à cet utilisateur qui ne l'a pas encore activée (§37).</summary>
    public bool MfaEnrollmentRequired { get; init; }
}

public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string? FullName,
    bool MfaEnabled,
    IReadOnlyList<CompanyMembershipResponse> Companies,
    IReadOnlyList<string> Permissions);

public sealed record CompanyMembershipResponse(
    Guid CompanyId,
    string CompanyName,
    Guid? GroupId,
    IReadOnlyList<string> Roles);

// --- MFA : enrôlement, confirmation, connexion, désactivation (Phase 33 §8-9) ------

/// <summary>Secret TOTP fraîchement généré — à afficher une seule fois (QR + saisie manuelle).</summary>
public sealed record MfaEnrollmentDto(string Secret, string OtpauthUri, string Issuer, string Account);

public sealed record MfaConfirmRequest(string Code);

/// <summary>Codes de récupération à usage unique — affichés une seule fois à la confirmation (§8).</summary>
public sealed record MfaRecoveryCodesDto(IReadOnlyList<string> RecoveryCodes);

/// <summary>Désactivation self-service : exige un code TOTP valide ou un code de récupération.</summary>
public sealed record MfaDisableRequest(string Code);

public sealed record MfaVerifyLoginRequest(string MfaToken, string Code);

public sealed record MfaStatusDto(
    bool Enabled,
    DateTimeOffset? EnrolledAt,
    int RecoveryCodesRemaining,
    bool PolicyRequires);
