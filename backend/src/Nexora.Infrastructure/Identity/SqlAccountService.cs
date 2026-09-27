using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Notifications;
using Nexora.Application.Security;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Identity;

/// <summary>
/// Mot de passe de l'utilisateur (recette Phase 55 AUTH-007/008).
/// <list type="bullet">
/// <item>Changement : exige le mot de passe actuel ; toutes les sessions tombent ensuite.</item>
/// <item>Oubli : réponse identique que le compte existe ou non (pas d'énumération), au plus
/// <see cref="MaxRequestsPerHour"/> demandes par compte et par heure, jeton à usage unique de
/// <see cref="TokenLifetime"/>, stocké haché, remis par le canal email (Phase 34) — jamais renvoyé
/// par l'API.</item>
/// <item>Réinitialisation : consomme le jeton, fixe le nouveau mot de passe, lève un éventuel
/// verrouillage anti-force-brute et fait tomber toutes les sessions.</item>
/// </list>
/// </summary>
public sealed class SqlAccountService(
    NexoraDbContext db,
    ITenantContext tenant,
    IPasswordHasher hasher,
    IDateTimeProvider clock,
    IEnumerable<INotificationChannel> channels,
    ISecurityEventLog events,
    ISessionCacheInvalidator sessionCache,
    ILogger<SqlAccountService> logger) : IAccountService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);
    public const int MaxRequestsPerHour = 3;

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record UserRow(Guid Id, string Email, string Status, string? PasswordHash);

    public async Task<Result> ChangePasswordAsync(ChangePasswordRequest request, string? ipAddress = null, CancellationToken ct = default)
    {
        if (tenant.UserId is not { } userId) return Error.Unauthorized("auth.required", "Authentification requise.");
        using var _ = tenant.BeginSystemScope();
        var user = await db.Database.SqlQuery<UserRow>($"""
            SELECT id AS "Id", email::text AS "Email", status AS "Status", password_hash AS "PasswordHash"
            FROM nexora.app_user WHERE id = {userId}
            """).FirstOrDefaultAsync(ct);
        if (user is null || user.Status != "active" || user.PasswordHash is null
            || !hasher.Verify(request.CurrentPassword ?? "", user.PasswordHash))
        {
            await events.RecordAsync("password.change_failed", userId, tenant.ActiveCompanyId, "mot de passe actuel incorrect", ipAddress, ct: ct);
            // 422 et non 401 : un 401 ferait déconnecter le client web sur une simple faute de frappe.
            return Error.Validation("auth.current_password", "Mot de passe actuel incorrect.");
        }
        if (PasswordPolicy.Validate(request.NewPassword, user.Email) is { } weak) return weak;
        if (hasher.Verify(request.NewPassword, user.PasswordHash))
            return Error.Validation("auth.password_reuse", "Le nouveau mot de passe doit être différent de l'actuel.");

        await SetPasswordAsync(userId, request.NewPassword, ct);
        await events.RecordAsync("password.changed", userId, tenant.ActiveCompanyId, ipAddress: ipAddress, ct: ct);
        return Result.Success();
    }

    public async Task RequestPasswordResetAsync(ForgotPasswordRequest request, string? ipAddress = null, CancellationToken ct = default)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        if (email.Length == 0) return;

        using var _ = tenant.BeginSystemScope();   // parcours anonyme : aucun contexte utilisateur
        var user = await db.Database.SqlQuery<UserRow>($"""
            SELECT id AS "Id", email::text AS "Email", status AS "Status", password_hash AS "PasswordHash"
            FROM nexora.app_user WHERE email = {email}::citext
            """).FirstOrDefaultAsync(ct);
        if (user is null || user.Status != "active") return;   // même réponse : pas d'énumération

        var recent = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.password_reset
            WHERE user_id = {user.Id} AND created_at > now() - interval '1 hour'
            """).FirstAsync(ct);
        if (recent >= MaxRequestsPerHour)
        {
            await events.RecordAsync("password.reset_throttled", user.Id, null, ipAddress: ipAddress, ct: ct);
            return;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = clock.UtcNow.Add(TokenLifetime);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.password_reset (user_id, token_hash, expires_at, requested_ip)
            VALUES ({user.Id}, {Hash(token)}, {expires}, {ipAddress}::inet)
            """, ct);
        await events.RecordAsync("password.reset_requested", user.Id, null, ipAddress: ipAddress, ct: ct);

        // Remise directe par le canal email : l'outbox est rattachée à une entreprise, ce parcours
        // n'en a pas. Le jeton ne transite jamais par la réponse HTTP.
        var mailChannel = channels.LastOrDefault(c => c.Channel == "email");
        if (mailChannel is null)
        {
            logger.LogWarning("Aucun canal email configuré : demande de réinitialisation {UserId} non remise.", user.Id);
            return;
        }
        var now = clock.UtcNow;
        var message = new OutboxMessageDto(
            Guid.NewGuid(), "email", "security.password_reset", 1,
            "NEXORA ONE — réinitialisation de votre mot de passe",
            $"Une réinitialisation de mot de passe a été demandée pour votre compte.\n\n" +
            $"Code de réinitialisation (valable {TokenLifetime.TotalMinutes:0} minutes, usage unique) :\n{token}\n\n" +
            $"Lien : /reset-password?token={token}\n\n" +
            "Si vous n'êtes pas à l'origine de cette demande, ignorez ce message : votre mot de passe actuel reste valable.",
            user.Email, "pending", 0, 1, now, null, Guid.NewGuid(), now, null);
        var sent = await mailChannel.SendAsync(message, ct);
        if (!sent.Sent) logger.LogWarning("Remise de l'email de réinitialisation {UserId} échouée : {Error}", user.Id, sent.Error);
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, string? ipAddress = null, CancellationToken ct = default)
    {
        var invalid = Error.Validation("auth.reset_token", "Code de réinitialisation invalide ou expiré.");
        if (string.IsNullOrWhiteSpace(request.Token)) return invalid;

        using var _ = tenant.BeginSystemScope();
        var user = await db.Database.SqlQuery<UserRow>($"""
            SELECT u.id AS "Id", u.email::text AS "Email", u.status AS "Status", u.password_hash AS "PasswordHash"
            FROM nexora.password_reset r JOIN nexora.app_user u ON u.id = r.user_id
            WHERE r.token_hash = {Hash(request.Token.Trim())} AND r.consumed_at IS NULL AND r.expires_at > now()
            """).FirstOrDefaultAsync(ct);
        if (user is null || user.Status != "active") return invalid;
        if (PasswordPolicy.Validate(request.NewPassword, user.Email) is { } weak) return weak;

        // Consommation atomique : un jeton rejoué en parallèle ne peut servir qu'une fois.
        var consumed = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.password_reset SET consumed_at = now()
            WHERE token_hash = {Hash(request.Token.Trim())} AND consumed_at IS NULL AND expires_at > now()
            """, ct);
        if (consumed == 0) return invalid;

        await SetPasswordAsync(user.Id, request.NewPassword, ct);
        // Les autres demandes encore ouvertes pour ce compte deviennent caduques.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.password_reset SET consumed_at = now() WHERE user_id = {user.Id} AND consumed_at IS NULL
            """, ct);
        await events.RecordAsync("password.reset", user.Id, null, ipAddress: ipAddress, ct: ct);
        return Result.Success();
    }

    /// <summary>
    /// Nouveau mot de passe + déconnexion globale + levée du verrouillage anti-force-brute.
    /// Invalide également le cache de révocation de session (REQ-02) pour que les tokens
    /// existants soient refusés dès la prochaine requête, sans attendre l'expiration du cache.
    /// </summary>
    private async Task SetPasswordAsync(Guid userId, string password, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.app_user
            SET password_hash = {hasher.Hash(password)}, security_epoch = now(), failed_login_count = 0, locked_until = NULL,
                updated_at = now()
            WHERE id = {userId}
            """, ct);

        // Invalidation immédiate du cache — les tokens de cet utilisateur seront refusés
        // à la prochaine requête, sans attendre les 30 s de TTL (REQ-02.2).
        sessionCache.InvalidateUser(userId);
    }
}
