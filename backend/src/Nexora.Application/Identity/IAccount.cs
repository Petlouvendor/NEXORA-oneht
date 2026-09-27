using Nexora.Contracts.Identity;
using Nexora.Domain.Common;

namespace Nexora.Application.Identity;

/// <summary>
/// Mot de passe de l'utilisateur (recette Phase 55 AUTH-007/008). Le parcours « oublié » ne
/// révèle jamais si un compte existe : la réponse est identique dans tous les cas.
/// Changer ou réinitialiser le mot de passe fait tomber toutes les sessions ouvertes (security_epoch).
/// </summary>
public interface IAccountService
{
    Task<Result> ChangePasswordAsync(ChangePasswordRequest request, string? ipAddress = null, CancellationToken ct = default);
    Task RequestPasswordResetAsync(ForgotPasswordRequest request, string? ipAddress = null, CancellationToken ct = default);
    Task<Result> ResetPasswordAsync(ResetPasswordRequest request, string? ipAddress = null, CancellationToken ct = default);
}

/// <summary>Politique de mot de passe unique (création, changement, réinitialisation).</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;

    public static Error? Validate(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return Error.Validation("auth.password_policy", $"Le mot de passe doit contenir au moins {MinLength} caractères.");
        if (password.Length > 128)
            return Error.Validation("auth.password_policy", "Le mot de passe ne peut dépasser 128 caractères.");
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return Error.Validation("auth.password_policy", "Le mot de passe doit contenir au moins une lettre et un chiffre.");
        if (!string.IsNullOrWhiteSpace(email) && password.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase)
            && email.Split('@')[0].Length >= 3)
            return Error.Validation("auth.password_policy", "Le mot de passe ne doit pas contenir l'identifiant du compte.");
        return null;
    }
}
