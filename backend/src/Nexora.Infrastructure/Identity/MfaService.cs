using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Security;
using Nexora.Contracts.Auth;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Identity;

/// <summary>
/// Authentification multi-facteur TOTP (Phase 33 §8-9). Le secret vit dans
/// <c>app_user.mfa_secret</c> (activé) ou <c>mfa_pending_secret</c> (enrôlement) ; les codes de
/// récupération sont hachés (SHA-256, usage unique) ; le défi de connexion transite par
/// <c>mfa_challenge</c>. Les opérations pré-authentifiées (défi) s'exécutent en portée système.
/// </summary>
public sealed class SqlMfaService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IDateTimeProvider clock,
    ISecurityEventLog events,
    IPermissionService permissions,
    IMfaSecretProtector protector) : IMfaService
{
    private const string Issuer = "Nexora One";   // libellé affiché dans l'application d'authentification
    private const int RecoveryCodeCount = 10;
    private const int MaxChallengeAttempts = 5;

    private Guid CurrentUser => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NormalizeRecovery(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record UserRow(
        string Email, bool MfaEnabled, string? MfaSecret, string? MfaPendingSecret, DateTimeOffset? MfaEnrolledAt);

    private async Task<UserRow?> LoadUserAsync(Guid userId, CancellationToken ct)
    {
        using var _ = tenant.BeginSystemScope();
        var row = await db.Database.SqlQuery<UserRow>($"""
            SELECT email AS "Email", mfa_enabled AS "MfaEnabled", mfa_secret AS "MfaSecret",
                   mfa_pending_secret AS "MfaPendingSecret", mfa_enrolled_at AS "MfaEnrolledAt"
            FROM nexora.app_user WHERE id = {userId}
            """).FirstOrDefaultAsync(ct);
        // §17-18 : le secret ne vit jamais en clair au repos — déchiffré uniquement en mémoire, ici.
        if (row is null) return null;
        return row with
        {
            MfaSecret = row.MfaSecret is { Length: > 0 } s ? protector.Unprotect(s) : row.MfaSecret,
            MfaPendingSecret = row.MfaPendingSecret is { Length: > 0 } p ? protector.Unprotect(p) : row.MfaPendingSecret,
        };
    }

    // ----------------------------------------------------------------- Statut

    public async Task<MfaStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var user = await LoadUserAsync(CurrentUser, ct)
            ?? throw new InvalidOperationException("Utilisateur introuvable.");
        var remaining = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.mfa_recovery_code
            WHERE user_id = {CurrentUser} AND used_at IS NULL
            """).FirstAsync(ct);
        return new MfaStatusDto(user.MfaEnabled, user.MfaEnrolledAt, remaining, await PolicyRequiresMfaAsync(CurrentUser, ct));
    }

    // ------------------------------------------------------------- Enrôlement

    public async Task<Result<MfaEnrollmentDto>> BeginEnrollmentAsync(CancellationToken ct = default)
    {
        var user = await LoadUserAsync(CurrentUser, ct);
        if (user is null) return Error.NotFound("mfa.user", "Utilisateur introuvable.");
        if (user.MfaEnabled) return Error.Conflict("mfa.already_enabled", "La MFA est déjà activée pour ce compte.");

        var secret = Totp.NewSecret();
        using (tenant.BeginSystemScope())
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.app_user SET mfa_pending_secret = {protector.Protect(secret)}, updated_at = now() WHERE id = {CurrentUser}
                """, ct);

        return Result.Success(new MfaEnrollmentDto(secret, Totp.BuildUri(secret, Issuer, user.Email), Issuer, user.Email));
    }

    public async Task<Result<MfaRecoveryCodesDto>> ConfirmEnrollmentAsync(MfaConfirmRequest request, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(CurrentUser, ct);
        if (user is null) return Error.NotFound("mfa.user", "Utilisateur introuvable.");
        if (user.MfaEnabled) return Error.Conflict("mfa.already_enabled", "La MFA est déjà activée.");
        if (string.IsNullOrWhiteSpace(user.MfaPendingSecret))
            return Error.Validation("mfa.no_enrollment", "Aucun enrôlement en cours — recommencez.");
        if (!Totp.Verify(user.MfaPendingSecret, request.Code, clock.UtcNow))
            return Error.Validation("mfa.invalid_code", "Code invalide. Vérifiez l'heure de votre appareil.");

        var plain = Enumerable.Range(0, RecoveryCodeCount).Select(_ => GenerateRecoveryCode()).ToList();

        await uow.ExecuteInTransactionAsync(async token =>
        {
            using var _ = tenant.BeginSystemScope();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.app_user
                SET mfa_enabled = true, mfa_secret = mfa_pending_secret, mfa_pending_secret = NULL,
                    mfa_enrolled_at = now(), updated_at = now()
                WHERE id = {CurrentUser}
                """, token);
            await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.mfa_recovery_code WHERE user_id = {CurrentUser}", token);
            foreach (var code in plain)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.mfa_recovery_code (user_id, code_hash) VALUES ({CurrentUser}, {Sha256(NormalizeRecovery(code))})
                    """, token);
            return 0;
        }, ct);

        await events.RecordAsync("mfa.enrolled", CurrentUser, tenant.ActiveCompanyId, ct: ct);
        return Result.Success(new MfaRecoveryCodesDto(plain));
    }

    // ------------------------------------------------------------ Désactivation

    public async Task<Result> DisableAsync(MfaDisableRequest request, CancellationToken ct = default)
    {
        var user = await LoadUserAsync(CurrentUser, ct);
        if (user is null) return Error.NotFound("mfa.user", "Utilisateur introuvable.");
        if (!user.MfaEnabled) return Error.Validation("mfa.not_enabled", "La MFA n'est pas activée.");

        if (!await VerifyCodeAsync(CurrentUser, user.MfaSecret, request.Code, ct))
        {
            await events.RecordAsync("mfa.disable_failed", CurrentUser, tenant.ActiveCompanyId, ct: ct);
            return Error.Unauthorized("mfa.invalid_code", "Code invalide.");
        }

        await ClearMfaAsync(CurrentUser, ct);
        await events.RecordAsync("mfa.disabled", CurrentUser, tenant.ActiveCompanyId, ct: ct);
        return Result.Success();
    }

    // ------------------------------------------------- Réinitialisation admin

    public async Task<Result> ResetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == CurrentUser)
            return Error.Validation("security.self_reset", "Utilisez la désactivation self-service pour votre propre compte.");

        var caller = await permissions.LoadProfileAsync(CurrentUser, ct);
        if (!caller.HasPermission("security.reset_mfa", null))
            return Error.Forbidden("security.forbidden", "Permission « security.reset_mfa » requise.");

        var target = await permissions.LoadProfileAsync(userId, ct);
        // La permission doit être détenue là où la cible travaille — une entreprise ou un groupe partagés —, pas
        // seulement « quelque part » (audit 2026-10, A-011).
        const string reset = "security.reset_mfa";
        var allowed = (caller.PlatformPermissions?.Contains(reset) ?? false)
            || caller.CompanyIds.Intersect(target.CompanyIds).Any(c => caller.HasPermission(reset, c))
            || caller.GroupIds.Intersect(target.GroupIds).Any(g => caller.GroupPermissionsFor(g).Contains(reset));
        if (!allowed)
            return Error.Forbidden("security.out_of_scope", "Utilisateur hors de votre périmètre (§39).");

        await ClearMfaAsync(userId, ct);
        using (tenant.BeginSystemScope())
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.mfa_challenge SET consumed_at = now()
                WHERE user_id = {userId} AND consumed_at IS NULL
                """, ct);

        await events.RecordAsync("mfa.reset_by_admin", userId, tenant.ActiveCompanyId,
            detail: $"réinitialisée par {CurrentUser}", ct: ct);
        return Result.Success();
    }

    // --------------------------------------------------- Défi de connexion

    public async Task<(string Token, DateTimeOffset ExpiresAt)> IssueChallengeAsync(Guid userId, Guid? companyId, CancellationToken ct = default)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = clock.UtcNow.AddMinutes(5);
        using (tenant.BeginSystemScope())
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.mfa_challenge SET consumed_at = now() WHERE user_id = {userId} AND consumed_at IS NULL
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.mfa_challenge (user_id, company_id, token_hash, expires_at)
                VALUES ({userId}, {companyId}, {Sha256(token)}, {expires})
                """, ct);
        }
        return (token, expires);
    }

    public async Task<Result<MfaLoginTicket>> VerifyChallengeAsync(string token, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return Error.Unauthorized("auth.mfa_challenge_invalid", "Défi manquant.");

        using var _ = tenant.BeginSystemScope();
        var challenge = await db.Database.SqlQuery<ChallengeRow>($"""
            SELECT id AS "Id", user_id AS "UserId", company_id AS "CompanyId", expires_at AS "ExpiresAt", attempts AS "Attempts"
            FROM nexora.mfa_challenge WHERE token_hash = {Sha256(token)} AND consumed_at IS NULL
            """).FirstOrDefaultAsync(ct);

        if (challenge is null) return Error.Unauthorized("auth.mfa_challenge_invalid", "Défi introuvable ou déjà utilisé.");
        if (challenge.ExpiresAt <= clock.UtcNow)
        {
            await ConsumeChallengeAsync(challenge.Id, ct);
            return Error.Unauthorized("auth.mfa_challenge_expired", "Défi expiré — reconnectez-vous.");
        }
        if (challenge.Attempts >= MaxChallengeAttempts)
        {
            await ConsumeChallengeAsync(challenge.Id, ct);
            await events.RecordAsync("mfa.challenge_locked", challenge.UserId, challenge.CompanyId, ct: ct);
            return Error.Unauthorized("auth.mfa_too_many_attempts", "Trop de tentatives — reconnectez-vous.");
        }

        var user = await LoadUserAsync(challenge.UserId, ct);
        if (user is null || !user.MfaEnabled)
        {
            await ConsumeChallengeAsync(challenge.Id, ct);
            return Error.Unauthorized("auth.mfa_challenge_invalid", "Défi invalide.");
        }

        if (!await VerifyCodeAsync(challenge.UserId, user.MfaSecret, code, ct))
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.mfa_challenge SET attempts = attempts + 1 WHERE id = {challenge.Id}
                """, ct);
            await events.RecordAsync("mfa.challenge_failed", challenge.UserId, challenge.CompanyId, ct: ct);
            return Error.Unauthorized("auth.mfa_invalid", "Code de vérification invalide.");
        }

        await ConsumeChallengeAsync(challenge.Id, ct);
        await events.RecordAsync("login.mfa_succeeded", challenge.UserId, challenge.CompanyId, ct: ct);
        return Result.Success(new MfaLoginTicket(challenge.UserId, challenge.CompanyId));
    }

    private sealed record ChallengeRow(Guid Id, Guid UserId, Guid? CompanyId, DateTimeOffset ExpiresAt, short Attempts);

    private async Task ConsumeChallengeAsync(Guid id, CancellationToken ct)
    {
        using var _ = tenant.BeginSystemScope();
        await db.Database.ExecuteSqlAsync($"UPDATE nexora.mfa_challenge SET consumed_at = now() WHERE id = {id}", ct);
    }

    // ----------------------------------------------------------------- Helpers

    /// <summary>Valide un code : d'abord TOTP, sinon un code de récupération à usage unique.</summary>
    private async Task<bool> VerifyCodeAsync(Guid userId, string? secret, string code, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(secret) && Totp.Verify(secret, code, clock.UtcNow)) return true;

        using var _ = tenant.BeginSystemScope();
        var consumed = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.mfa_recovery_code SET used_at = now()
            WHERE user_id = {userId} AND code_hash = {Sha256(NormalizeRecovery(code))} AND used_at IS NULL
            """, ct);
        return consumed > 0;
    }

    private async Task ClearMfaAsync(Guid userId, CancellationToken ct)
    {
        await uow.ExecuteInTransactionAsync(async token =>
        {
            using var _ = tenant.BeginSystemScope();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.app_user
                SET mfa_enabled = false, mfa_secret = NULL, mfa_pending_secret = NULL, mfa_enrolled_at = NULL, updated_at = now()
                WHERE id = {userId}
                """, token);
            await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.mfa_recovery_code WHERE user_id = {userId}", token);
            return 0;
        }, ct);
    }

    public async Task<bool> VerifyCurrentUserCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var user = await LoadUserAsync(CurrentUser, ct);
        return user is { MfaEnabled: true } && await VerifyCodeAsync(CurrentUser, user.MfaSecret, code.Trim(), ct);
    }

    public async Task<bool> IsEnabledForCurrentUserAsync(CancellationToken ct = default)
        => (await LoadUserAsync(CurrentUser, ct))?.MfaEnabled == true;

    public async Task<bool> PolicyRequiresMfaAsync(Guid userId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        // Un rôle attribué au groupe (la direction, par exemple) relève de la politique de chacune de ses
        // sociétés : il échappait à l'obligation, la jointure ne regardant que les rôles d'une société.
        return await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1
                FROM nexora.user_role ur
                JOIN nexora.role r          ON r.id = ur.role_id
                JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                JOIN nexora.security_policy sp ON sp.company_id = sc.company_id
                     OR sp.company_id IN (SELECT c.id FROM nexora.company c WHERE c.group_id = sc.group_id)
                WHERE ur.user_id = {userId}
                  AND ur.revoked_at IS NULL
                  AND (ur.valid_from IS NULL OR ur.valid_from <= now())
                  AND (ur.valid_until IS NULL OR ur.valid_until >= now())
                  AND sp.mfa_required_roles ? r.name
            ) AS "Value"
            """).FirstAsync(ct);
    }

    public async Task<bool> PolicyEnforcesMfaAsync(Guid userId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        // Un rôle attribué au groupe (la direction, par exemple) relève de la politique de chacune de ses
        // sociétés : il échappait à l'obligation, la jointure ne regardant que les rôles d'une société.
        return await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1
                FROM nexora.user_role ur
                JOIN nexora.role r          ON r.id = ur.role_id
                JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                JOIN nexora.security_policy sp ON sp.company_id = sc.company_id
                     OR sp.company_id IN (SELECT c.id FROM nexora.company c WHERE c.group_id = sc.group_id)
                WHERE ur.user_id = {userId}
                  AND ur.revoked_at IS NULL
                  AND (ur.valid_from IS NULL OR ur.valid_from <= now())
                  AND (ur.valid_until IS NULL OR ur.valid_until >= now())
                  AND sp.mfa_required_roles ? r.name AND sp.enforce_mfa_for_critical
            ) AS "Value"
            """).FirstAsync(ct);
    }

    private static readonly char[] RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private static string GenerateRecoveryCode()
    {
        Span<char> buffer = stackalloc char[10];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        return $"{new string(buffer[..5])}-{new string(buffer[5..])}";
    }
}
