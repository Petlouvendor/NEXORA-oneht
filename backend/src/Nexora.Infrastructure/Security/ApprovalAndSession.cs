using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Security;
using Nexora.Contracts.Security;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Security;

/// <summary>Workflow d'approbation des opérations sensibles (Phase 11 §19-20). Réutilise V016.</summary>
public sealed class SqlApprovalWorkflowService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : IApprovalWorkflowService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private static readonly string[] Types =
        ["expense", "discount", "refund", "purchase", "credit_sale", "stock_adjustment", "price_change", "void", "period_close", "other"];

    private sealed record PolicyRow(decimal MinAmount, short RequiredLevels);
    private sealed record RequestRow(
        Guid Id, string OperationType, string? ReferenceType, Guid? ReferenceId, decimal? Amount, Guid? RequestedBy,
        string Status, short CurrentLevel, short RequiredLevels, DateTimeOffset RequestedAt);
    private sealed record RoleRequirement(Guid RoleId, string RoleName);
    private sealed record ActionRow(Guid? ApproverId, short Level, string Action, string? Comment, DateTimeOffset At);

    public async Task<Result<ApprovalOutcome>> SubmitAsync(SubmitApprovalRequest request, CancellationToken ct = default)
    {
        var type = request.OperationType.Trim().ToLowerInvariant();
        if (!Types.Contains(type)) return Error.Validation("security.approval_type", $"Type d'opération « {type} » invalide.");
        var amount = request.Amount ?? 0m;

        var policy = await db.Database.SqlQuery<PolicyRow>($"""
            SELECT min_amount AS "MinAmount", required_levels AS "RequiredLevels"
            FROM nexora.approval_policy
            WHERE company_id = {Company} AND request_type = {type} AND status = 'active' AND min_amount <= {amount}
            ORDER BY min_amount DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);

        if (policy is null)
            return Result.Success(new ApprovalOutcome(false, null, "not_required", 0, 0, "Aucune approbation requise pour cette opération."));

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.approval_request
                (id, company_id, request_type, reference_type, reference_id, requested_by, amount, status, current_level, required_levels)
            VALUES ({id}, {Company}, {type}, {request.ReferenceType}, {request.ReferenceId}, {tenant.UserId}, {request.Amount},
                    'pending', 1, {policy.RequiredLevels})
            """, ct);
        if (!string.IsNullOrWhiteSpace(request.Reason))
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.approval_action (company_id, approval_request_id, approver_id, level, action, comment)
                VALUES ({Company}, {id}, {tenant.UserId}, 0, 'comment', {request.Reason})
                """, ct);
        await uow.SaveChangesAsync(ct);

        return Result.Success(new ApprovalOutcome(true, id, "pending", 1, policy.RequiredLevels,
            policy.RequiredLevels > 1 ? "Double approbation requise (§5)." : "Approbation requise."));
    }

    public async Task<Result<ApprovalRequestDto>> DecideAsync(Guid requestId, ApprovalDecisionRequest decision, CancellationToken ct = default)
    {
        var req = await db.Database.SqlQuery<RequestRow>($"""
            SELECT id AS "Id", request_type AS "OperationType", reference_type AS "ReferenceType", reference_id AS "ReferenceId",
                   amount AS "Amount", requested_by AS "RequestedBy", status AS "Status", current_level AS "CurrentLevel",
                   required_levels AS "RequiredLevels", requested_at AS "RequestedAt"
            FROM nexora.approval_request WHERE id = {requestId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (req is null) return Error.NotFound("security.approval", "Demande introuvable.");
        if (req.Status != "pending") return Error.Conflict("security.approval_closed", "Demande déjà traitée.");

        // Séparation des responsabilités : le demandeur ne s'approuve pas (§5).
        if (req.RequestedBy is { } rb && rb == tenant.UserId)
            return Error.Forbidden("security.self_approval", "Le demandeur ne peut pas approuver sa propre demande (§5).");

        // Double approbation : deux approbateurs distincts (§5).
        var alreadyApprover = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.approval_action
                          WHERE approval_request_id = {requestId} AND approver_id = {tenant.UserId} AND action = 'approve') AS "Value"
            """).FirstAsync(ct);
        if (decision.Approve && alreadyApprover)
            return Error.Forbidden("security.duplicate_approver", "Un même approbateur ne peut pas valider deux niveaux (§5).");

        // Finalisation phase 11 : le rôle approbateur d'une règle (workflow) était enregistré mais jamais vérifié.
        var requiredRole = await db.Database.SqlQuery<RoleRequirement>($"""
            SELECT p.approver_role_id AS "RoleId", r.name AS "RoleName" FROM nexora.approval_policy p
            JOIN nexora.role r ON r.id = p.approver_role_id
            WHERE p.company_id = {Company} AND p.request_type = {req.OperationType} AND p.status = 'active'
              AND p.min_amount <= {req.Amount ?? 0m}
            ORDER BY p.min_amount DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (requiredRole is not null && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.user_role ur JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                              WHERE ur.user_id = {tenant.UserId} AND ur.role_id = {requiredRole.RoleId} AND ur.revoked_at IS NULL
                                AND (ur.valid_from IS NULL OR ur.valid_from <= now()) AND (ur.valid_until IS NULL OR ur.valid_until > now())
                                AND (sc.company_id = {Company}
                                     OR (sc.group_id IS NOT NULL AND sc.group_id = (SELECT group_id FROM nexora.company WHERE id = {Company})))) AS "Value"
                """).FirstAsync(ct))
            return Error.Forbidden("security.approver_role", $"Cette demande doit être décidée par un utilisateur ayant le rôle « {requiredRole.RoleName} ».");

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.approval_action (company_id, approval_request_id, approver_id, level, action, comment)
                VALUES ({Company}, {requestId}, {tenant.UserId}, {req.CurrentLevel}, {(decision.Approve ? "approve" : "reject")}, {decision.Note})
                """, token);

            if (!decision.Approve)
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.approval_request SET status = 'rejected', completed_at = now(), updated_at = now()
                    WHERE id = {requestId}
                    """, token);
            }
            else if (req.CurrentLevel >= req.RequiredLevels)
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.approval_request SET status = 'approved', completed_at = now(), updated_at = now()
                    WHERE id = {requestId}
                    """, token);
            }
            else
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.approval_request SET current_level = current_level + 1, updated_at = now()
                    WHERE id = {requestId}
                    """, token);
            }
            return 0;
        }, ct);

        return await GetAsync(requestId, ct);
    }

    public async Task<IReadOnlyList<ApprovalRequestDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.approval_request
            WHERE company_id = {Company} AND ({status}::text IS NULL OR status = {status}::text)
            ORDER BY requested_at DESC LIMIT 200
            """).ToListAsync(ct);
        var list = new List<ApprovalRequestDto>();
        foreach (var id in ids)
            if ((await GetAsync(id, ct)) is { IsSuccess: true } r) list.Add(r.Value!);
        return list;
    }

    public async Task<Result<ApprovalRequestDto>> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        var req = await db.Database.SqlQuery<RequestRow>($"""
            SELECT id AS "Id", request_type AS "OperationType", reference_type AS "ReferenceType", reference_id AS "ReferenceId",
                   amount AS "Amount", requested_by AS "RequestedBy", status AS "Status", current_level AS "CurrentLevel",
                   required_levels AS "RequiredLevels", requested_at AS "RequestedAt"
            FROM nexora.approval_request WHERE id = {requestId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (req is null) return Error.NotFound("security.approval", "Demande introuvable.");

        var actions = await db.Database.SqlQuery<ActionRow>($"""
            SELECT approver_id AS "ApproverId", level AS "Level", action AS "Action", comment AS "Comment", created_at AS "At"
            FROM nexora.approval_action WHERE approval_request_id = {requestId} ORDER BY created_at
            """).ToListAsync(ct);

        return Result.Success(new ApprovalRequestDto(
            req.Id, req.OperationType, req.ReferenceType, req.ReferenceId, req.Amount, req.RequestedBy,
            req.Status, req.CurrentLevel, req.RequiredLevels, req.RequestedAt,
            actions.Select(a => new ApprovalActionDto(a.ApproverId, a.Level, a.Action, a.Comment, a.At)).ToList()));
    }
}

/// <summary>Ré-authentification renforcée (Phase 11 §7, §36).</summary>
public sealed class SqlStepUpAuthService(
    NexoraDbContext db, ITenantContext tenant, IPasswordHasher hasher, IDateTimeProvider clock,
    Nexora.Application.Identity.IMfaService mfa, ISecurityEventLog events) : IStepUpAuthService
{
    private Guid UserId => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");
    private static readonly string[] Purposes =
        ["payment", "cancellation", "accounting_validation", "permission_change", "deletion", "sensitive_data",
         "period_close", "period_reopen", "financial_exceptional", "consolidation_publish", "device_revoke", "sensitive_export", "other"];
    private const int MaxFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private async Task FailAsync(string purpose, string reason, string? ip, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.step_up_failure (user_id, purpose, reason, ip_address) VALUES ({UserId}, {purpose}, {reason}, {ip}::inet)
            """, ct);
        await events.RecordAsync("step_up.failed", UserId, tenant.ActiveCompanyId, ipAddress: ip,
            context: new Dictionary<string, object?> { ["purpose"] = purpose, ["reason"] = reason }, ct: ct);
    }

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<Result<StepUpChallengeResponse>> ChallengeAsync(StepUpChallengeRequest request, CancellationToken ct = default)
    {
        var purpose = (request.Purpose ?? "").Trim().ToLowerInvariant();
        if (!Purposes.Contains(purpose)) return Error.Validation("security.step_up_purpose", "Motif de ré-authentification invalide.");

        // Les essais ne sont plus illimités : 5 échecs en 15 minutes verrouillent la réauthentification.
        var since = clock.UtcNow - FailureWindow;
        var failures = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.step_up_failure WHERE user_id = {UserId} AND created_at > {since}
            """).FirstAsync(ct);
        if (failures >= MaxFailures)
            return Error.Forbidden("security.step_up_locked", "Trop d'échecs de confirmation : réessayez dans 15 minutes.");

        // Rôle soumis à la double authentification (politique) : pas d'action critique sans elle.
        var mfaEnabled = await mfa.IsEnabledForCurrentUserAsync(ct);
        if (!mfaEnabled && await mfa.PolicyEnforcesMfaAsync(UserId, ct))
            return Error.Forbidden("security.mfa_enrollment_required",
                "Votre rôle exige la double authentification : activez-la dans « Mon compte » avant cette action.");

        var hash = await db.Database.SqlQuery<string>($"""
            SELECT password_hash AS "Value" FROM nexora.app_user WHERE id = {UserId} AND status = 'active'
            """).FirstOrDefaultAsync(ct);
        if (hash is null || !hasher.Verify(request.Password ?? "", hash))
        {
            await FailAsync(purpose, "password", request.IpAddress, ct);
            return Error.Validation("security.step_up_failed", "Mot de passe incorrect.");
        }
        if (mfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.Code))
                return Error.Validation("security.step_up_code_required", "Saisissez le code de votre application d'authentification.");
            if (!await mfa.VerifyCurrentUserCodeAsync(request.Code, ct))
            {
                await FailAsync(purpose, "mfa_code", request.IpAddress, ct);
                return Error.Validation("security.step_up_failed", "Code de double authentification incorrect.");
            }
        }

        var ttl = await db.Database.SqlQuery<int>($"""
            SELECT COALESCE((SELECT step_up_ttl_minutes FROM nexora.security_policy WHERE company_id = {tenant.ActiveCompanyId}), 10) AS "Value"
            """).FirstAsync(ct);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = clock.UtcNow.AddMinutes(ttl);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.step_up_challenge (user_id, purpose, token_hash, expires_at, session_id, method)
            VALUES ({UserId}, {purpose}, {Hash(token)}, {expires}, {request.SessionId}, {(mfaEnabled ? "password+mfa" : "password")})
            """, ct);
        await events.RecordAsync("step_up.granted", UserId, tenant.ActiveCompanyId, ipAddress: request.IpAddress,
            context: new Dictionary<string, object?> { ["purpose"] = purpose, ["method"] = mfaEnabled ? "password+mfa" : "password" }, ct: ct);

        return Result.Success(new StepUpChallengeResponse(token, purpose, expires));
    }

    public async Task<bool> ValidateAsync(string token, string purpose, Guid? sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || tenant.UserId is not { } uid) return false;
        var p = (purpose ?? "").Trim().ToLowerInvariant();
        return await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.step_up_challenge
                          WHERE user_id = {uid} AND purpose = {p} AND token_hash = {Hash(token.Trim())}
                            AND consumed_at IS NULL AND expires_at > now()
                            AND (session_id IS NULL OR {sessionId}::uuid IS NULL OR session_id = {sessionId})) AS "Value"
            """).FirstAsync(ct);
    }

    public async Task<bool> VerifyAsync(StepUpVerifyRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) return false;
        var purpose = request.Purpose.Trim().ToLowerInvariant();
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.step_up_challenge SET consumed_at = now()
            WHERE user_id = {UserId} AND purpose = {purpose} AND token_hash = {Hash(request.Token)}
              AND consumed_at IS NULL AND expires_at > now()
            """, ct);
        return n > 0;
    }
}

/// <summary>Registre des sessions et révocation, dont la déconnexion globale (Phase 11 §6, §30).</summary>
public sealed class SqlSessionSecurityService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant,
    ISessionCacheInvalidator sessionCache, Microsoft.Extensions.Configuration.IConfiguration configuration,
    IDateTimeProvider clock, IUserRepository users, Nexora.Application.UseCases.Auth.AccessTokenIssuer issuer,
    Nexora.Application.Identity.IMfaService mfa, ISecurityEventLog events) : ISessionSecurityService
{
    private Guid UserId => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    /// <summary>Durée maximale d'une session : au-delà, une nouvelle connexion est exigée (Security:SessionMaxHours, 12 h).</summary>
    private TimeSpan SessionMax => TimeSpan.FromHours(Math.Clamp(int.TryParse(configuration["Security:SessionMaxHours"], out var h) ? h : 12, 1, 24 * 30));

    /// <summary>Délai de grâce : deux onglets qui rafraîchissent au même instant ne sont pas pris pour un vol.</summary>
    private static readonly TimeSpan RaceGrace = TimeSpan.FromSeconds(20);

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewRefreshToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<SessionStart> StartAsync(Guid sessionId, Guid userId, Guid? activeCompanyId, string? ipAddress, string? userAgent,
        Guid? tokenId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();   // pas encore de contexte tenant à l'ouverture de session
        var expires = clock.UtcNow.Add(SessionMax);
        var refresh = NewRefreshToken();
        var label = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent[..Math.Min(userAgent.Length, 200)];
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.user_session (id, user_id, active_company_id, device_label, ip_address, user_agent, expires_at,
                                                 device_type, token_id, refresh_expires_at, last_seen_at)
                VALUES ({sessionId}, {userId}, {activeCompanyId}, {label}, {ipAddress}::inet, {userAgent}, {expires},
                        {InferDeviceType(userAgent)}, {tokenId}, {expires}, now())
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.auth_refresh_token (session_id, user_id, token_hash, expires_at, created_ip)
                VALUES ({sessionId}, {userId}, {HashToken(refresh)}, {expires}, {ipAddress}::inet)
                """, token);
            return 0;
        }, ct);
        return new SessionStart(refresh, expires);
    }

    private sealed record RefreshRow(
        Guid TokenId, Guid SessionId, Guid UserId, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt, DateTimeOffset? RevokedAt,
        DateTimeOffset? SessionRevokedAt, DateTimeOffset SessionExpiresAt, DateTimeOffset LastSeenAt, DateTimeOffset IssuedAt,
        Guid? ActiveCompanyId, string UserStatus, DateTimeOffset Epoch, int IdleMinutes);

    public async Task<Result<RefreshOutcome>> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 200)
            return Error.Unauthorized("auth.refresh_invalid", "Session expirée — reconnectez-vous.");
        using var _ = tenant.BeginSystemScope();
        var now = clock.UtcNow;
        var row = await db.Database.SqlQuery<RefreshRow>($"""
            SELECT t.id AS "TokenId", t.session_id AS "SessionId", t.user_id AS "UserId", t.expires_at AS "ExpiresAt",
                   t.used_at AS "UsedAt", t.revoked_at AS "RevokedAt", s.revoked_at AS "SessionRevokedAt",
                   s.expires_at AS "SessionExpiresAt", s.last_seen_at AS "LastSeenAt", s.issued_at AS "IssuedAt",
                   s.active_company_id AS "ActiveCompanyId", u.status AS "UserStatus", u.security_epoch AS "Epoch",
                   COALESCE((SELECT sp.session_ttl_minutes FROM nexora.security_policy sp WHERE sp.company_id = s.active_company_id),
                            (SELECT MIN(sp.session_ttl_minutes) FROM nexora.security_policy sp
                              JOIN nexora.user_role ur ON ur.user_id = u.id AND ur.revoked_at IS NULL
                              JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                              WHERE sp.company_id = sc.company_id
                                 OR sp.company_id IN (SELECT c.id FROM nexora.company c WHERE c.group_id = sc.group_id)),
                            60) AS "IdleMinutes"
            FROM nexora.auth_refresh_token t
            JOIN nexora.user_session s ON s.id = t.session_id
            JOIN nexora.app_user u ON u.id = t.user_id
            WHERE t.token_hash = {HashToken(refreshToken.Trim())}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.Unauthorized("auth.refresh_invalid", "Session expirée — reconnectez-vous.");

        if (row.UsedAt is { } used)
        {
            // Rafraîchissement simultané de deux onglets : le second échoue sans révoquer (il reprend le nouveau cookie).
            if (now - used < RaceGrace && row.RevokedAt is null)
                return Error.Unauthorized("auth.refresh_race", "Session renouvelée par un autre onglet.");
            // Jeton déjà consommé présenté de nouveau : vol probable. Toutes les sessions de l'utilisateur tombent.
            await CompromiseAsync(row.UserId, row.SessionId, ipAddress, ct);
            return Error.Unauthorized("auth.session_compromised",
                "Sécurité : ce jeton de session a déjà servi. Toutes vos sessions ont été fermées — reconnectez-vous.");
        }
        if (row.RevokedAt is not null || row.SessionRevokedAt is not null || row.UserStatus != "active"
            || row.IssuedAt < row.Epoch.AddSeconds(-5))
            return Error.Unauthorized("auth.session_revoked", "Session fermée — reconnectez-vous.");
        if (now >= row.ExpiresAt || now >= row.SessionExpiresAt)
            return Error.Unauthorized("auth.session_expired", "Durée maximale de session atteinte — reconnectez-vous.");
        if (now - row.LastSeenAt > TimeSpan.FromMinutes(Math.Max(5, row.IdleMinutes)))
        {
            await RevokeSessionAsync(row.SessionId, "inactivité", ct);
            return Error.Unauthorized("auth.session_idle", "Session fermée après une période d'inactivité — reconnectez-vous.");
        }

        var user = await users.FindByIdAsync(row.UserId, ct);
        if (user is null) return Error.Unauthorized("auth.session_revoked", "Session fermée — reconnectez-vous.");

        // Consommation atomique : un seul rafraîchissement gagne.
        var claimed = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.auth_refresh_token SET used_at = now() WHERE id = {row.TokenId} AND used_at IS NULL AND revoked_at IS NULL
            """, ct);
        if (claimed == 0) return Error.Unauthorized("auth.refresh_race", "Session renouvelée par un autre onglet.");

        var next = NewRefreshToken();
        var nextId = Guid.NewGuid();
        var response = await issuer.IssueAsync(user, row.ActiveCompanyId, await mfa.PolicyRequiresMfaAsync(user.Id, ct) && !user.MfaEnabled,
            ct, row.SessionId, isRefresh: true);
        Guid? jti = Guid.TryParse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(response.AccessToken).Id, out var j) ? j : null;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.auth_refresh_token (id, session_id, user_id, token_hash, expires_at, created_ip)
            VALUES ({nextId}, {row.SessionId}, {row.UserId}, {HashToken(next)}, {row.SessionExpiresAt}, {ipAddress}::inet)
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.auth_refresh_token SET replaced_by = {nextId} WHERE id = {row.TokenId}
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.user_session SET token_id = {jti}, last_seen_at = now(), updated_at = now() WHERE id = {row.SessionId}
            """, ct);
        return Result.Success(new RefreshOutcome(response with { SessionExpiresAt = row.SessionExpiresAt }, next, row.SessionExpiresAt));
    }

    private async Task CompromiseAsync(Guid userId, Guid sessionId, string? ip, CancellationToken ct)
    {
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.user_session SET revoked_at = now(), revoke_reason = 'session compromise : jeton de rafraîchissement réutilisé', updated_at = now()
                WHERE user_id = {userId} AND revoked_at IS NULL
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.auth_refresh_token SET revoked_at = now() WHERE user_id = {userId} AND revoked_at IS NULL
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.app_user SET security_epoch = now(), updated_at = now() WHERE id = {userId}
                """, token);
            return 0;
        }, ct);
        sessionCache.InvalidateUser(userId);
        await events.RecordAsync("session.compromised", userId, null, "Jeton de rafraîchissement réutilisé après rotation", ip,
            new Dictionary<string, object?> { ["session"] = sessionId }, ct);
    }

    public async Task RevokeByRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 200) return;
        using var _ = tenant.BeginSystemScope();
        var sessionId = await db.Database.SqlQuery<Guid?>($"""
            SELECT session_id AS "Value" FROM nexora.auth_refresh_token WHERE token_hash = {HashToken(refreshToken.Trim())}
            """).FirstOrDefaultAsync(ct);
        if (sessionId is { } sid) await RevokeSessionAsync(sid, "déconnexion", ct);
    }

    public async Task RevokeSessionAsync(Guid sessionId, string reason, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var tokens = await db.Database.SqlQuery<Guid?>($"""
            UPDATE nexora.user_session SET revoked_at = now(), revoked_by = {tenant.UserId}, revoke_reason = {reason}, updated_at = now()
            WHERE id = {sessionId} AND revoked_at IS NULL
            RETURNING token_id AS "Value"
            """).ToListAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.auth_refresh_token SET revoked_at = now() WHERE session_id = {sessionId} AND revoked_at IS NULL
            """, ct);
        foreach (var t in tokens) if (t is { } tid) sessionCache.Invalidate(tid);
    }
    private sealed record SessionRow(
        Guid Id, Guid? ActiveCompanyId, string? DeviceLabel, string? IpAddress,
        DateTimeOffset IssuedAt, DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt, string? RevokeReason,
        string DeviceType, bool Trusted);

    /// <summary>Un appareil (Phase 47 §11) EST une session — la seule information qui manquait était son type.</summary>
    private static string InferDeviceType(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "unknown";
        var ua = userAgent.ToLowerInvariant();
        if (ua.Contains("nexora-pos") || ua.Contains("pos-terminal")) return "pos_terminal";
        if (ua.Contains("okhttp") || ua.Contains("nexora-mobile") || (ua.Contains("mobile") && !ua.Contains("ipad"))) return "mobile";
        if (ua.Contains("ipad") || ua.Contains("tablet")) return "tablet";
        if (ua.Contains("mozilla") || ua.Contains("chrome") || ua.Contains("safari") || ua.Contains("firefox") || ua.Contains("edg/")) return "desktop";
        return "api";
    }

    public async Task RecordAsync(Guid userId, Guid? activeCompanyId, string? deviceLabel, string? ipAddress, string? userAgent, Guid? tokenId = null, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();   // pas encore de contexte tenant à l'ouverture de session
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.user_session (user_id, active_company_id, device_label, ip_address, user_agent, expires_at, device_type, token_id)
            VALUES ({userId}, {activeCompanyId}, {deviceLabel}, {ipAddress}::inet, {userAgent}, now() + interval '12 hours', {InferDeviceType(userAgent)}, {tokenId})
            """, ct);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<Result> RevokeTokenAsync(Guid tokenId, string reason, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.user_session SET revoked_at = now(), revoked_by = {UserId}, revoke_reason = {reason}, updated_at = now()
            WHERE token_id = {tokenId} AND user_id = {UserId} AND revoked_at IS NULL
            """, ct);
        sessionCache.Invalidate(tokenId);   // le jeton est refusé dès la requête suivante
        return n == 0 ? Error.NotFound("security.session", "Session introuvable ou déjà révoquée.") : Result.Success();
    }

    public async Task<IReadOnlyList<UserSessionDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<SessionRow>($"""
            SELECT id AS "Id", active_company_id AS "ActiveCompanyId", device_label AS "DeviceLabel",
                   host(ip_address) AS "IpAddress", issued_at AS "IssuedAt", last_seen_at AS "LastSeenAt",
                   revoked_at AS "RevokedAt", revoke_reason AS "RevokeReason", device_type AS "DeviceType", trusted AS "Trusted"
            FROM nexora.user_session WHERE user_id = {UserId} ORDER BY issued_at DESC LIMIT 100
            """).ToListAsync(ct);
        var mostRecentActive = rows.FirstOrDefault(r => r.RevokedAt is null)?.Id;
        return rows.Select(r => new UserSessionDto(r.Id, r.ActiveCompanyId, r.DeviceLabel, r.IpAddress,
            r.IssuedAt, r.LastSeenAt, r.RevokedAt, r.RevokeReason, r.Id == mostRecentActive, r.DeviceType, r.Trusted)).ToList();
    }

    public async Task<Result> SetTrustedAsync(Guid sessionId, bool trusted, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.user_session SET trusted = {trusted}, updated_at = now()
            WHERE id = {sessionId} AND user_id = {UserId} AND revoked_at IS NULL
            """, ct);
        return n == 0 ? Error.NotFound("security.session", "Session introuvable ou révoquée.") : Result.Success();
    }

    public async Task<Result> RevokeAsync(Guid sessionId, RevokeSessionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return Error.Validation("security.revoke_reason", "Motif requis (§30).");
        var tokenIds = await db.Database.SqlQuery<Guid?>($"""
            UPDATE nexora.user_session SET revoked_at = now(), revoked_by = {UserId}, revoke_reason = {request.Reason}, updated_at = now()
            WHERE id = {sessionId} AND user_id = {UserId} AND revoked_at IS NULL
            RETURNING token_id AS "Value"
            """).ToListAsync(ct);
        // Session d'un autre utilisateur (ou déjà révoquée) : rien d'autre ne bouge. Avant (audit 2026-10, A-032), ses
        // jetons de rafraîchissement étaient révoqués quand même — déconnexion forcée d'autrui par son identifiant.
        if (tokenIds.Count == 0) return Error.NotFound("security.session", "Session introuvable ou déjà révoquée.");
        foreach (var t in tokenIds) if (t is { } tid) sessionCache.Invalidate(tid);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.auth_refresh_token SET revoked_at = now() WHERE session_id = {sessionId} AND revoked_at IS NULL
            """, ct);
        return Result.Success();
    }

    public async Task<Result> RevokeAllAsync(RevokeSessionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return Error.Validation("security.revoke_reason", "Motif requis (§30).");
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.user_session SET revoked_at = now(), revoked_by = {UserId},
                    revoke_reason = {request.Reason}, updated_at = now()
                WHERE user_id = {UserId} AND revoked_at IS NULL
                """, token);
            // Déconnexion globale : tout jeton émis avant maintenant est invalidé (§6).
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.app_user SET security_epoch = now(), updated_at = now() WHERE id = {UserId}
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.auth_refresh_token SET revoked_at = now() WHERE user_id = {UserId} AND revoked_at IS NULL
                """, token);
            return 0;
        }, ct);
        sessionCache.InvalidateUser(UserId);
        return Result.Success();
    }
}

/// <summary>Politique de sécurité par entreprise (Phase 11 §7, §21, §31).</summary>
public sealed class SqlSecurityPolicyService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : ISecurityPolicyService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private sealed record PolicyRow(
        string MfaRequiredRoles, int SessionTtlMinutes, int StepUpTtlMinutes,
        bool SensitiveExportRequiresApproval, bool ExportWatermark, int AuditRetentionDays, bool EnforceMfaForCritical);

    public async Task<SecurityPolicyDto> GetAsync(CancellationToken ct = default)
    {
        var p = await db.Database.SqlQuery<PolicyRow>($"""
            SELECT mfa_required_roles::text AS "MfaRequiredRoles", session_ttl_minutes AS "SessionTtlMinutes",
                   step_up_ttl_minutes AS "StepUpTtlMinutes", sensitive_export_requires_approval AS "SensitiveExportRequiresApproval",
                   export_watermark AS "ExportWatermark", audit_retention_days AS "AuditRetentionDays",
                   enforce_mfa_for_critical AS "EnforceMfaForCritical"
            FROM nexora.security_policy WHERE company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (p is null)
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO nexora.security_policy (company_id) VALUES ({Company}) ON CONFLICT DO NOTHING", ct);
            return new SecurityPolicyDto(Company, ["super_administrator", "group_administrator", "owner_direction", "chief_accountant"], 60, 10, false, true, 2555);
        }
        return new SecurityPolicyDto(Company,
            System.Text.Json.JsonSerializer.Deserialize<List<string>>(p.MfaRequiredRoles) ?? [],
            p.SessionTtlMinutes, p.StepUpTtlMinutes, p.SensitiveExportRequiresApproval, p.ExportWatermark, p.AuditRetentionDays,
            p.EnforceMfaForCritical);
    }

    public async Task<Result> SetAsync(SetSecurityPolicyRequest r, CancellationToken ct = default)
    {
        if (r.SessionTtlMinutes is <= 0 || r.StepUpTtlMinutes is <= 0)
            return Error.Validation("security.ttl", "Les durées doivent être positives.");
        // Bornes : une inactivité de plus d'une journée ou une confirmation forte valable plus d'une heure
        // affaiblissent la sécurité au lieu de la régler.
        if (r.SessionTtlMinutes is > 1440) return Error.Validation("security.ttl", "Inactivité maximale : 1 440 minutes (24 h).");
        if (r.StepUpTtlMinutes is > 60) return Error.Validation("security.ttl", "Confirmation forte : 60 minutes au plus.");
        if (r.AuditRetentionDays is < 0) return Error.Validation("security.retention", "Rétention négative.");
        var rolesJson = r.MfaRequiredRoles is null ? null : System.Text.Json.JsonSerializer.Serialize(r.MfaRequiredRoles);

        await db.Database.ExecuteSqlAsync($"INSERT INTO nexora.security_policy (company_id) VALUES ({Company}) ON CONFLICT DO NOTHING", ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.security_policy SET
                mfa_required_roles = COALESCE({rolesJson}::jsonb, mfa_required_roles),
                session_ttl_minutes = COALESCE({r.SessionTtlMinutes}, session_ttl_minutes),
                step_up_ttl_minutes = COALESCE({r.StepUpTtlMinutes}, step_up_ttl_minutes),
                sensitive_export_requires_approval = COALESCE({r.SensitiveExportRequiresApproval}, sensitive_export_requires_approval),
                export_watermark = COALESCE({r.ExportWatermark}, export_watermark),
                audit_retention_days = COALESCE({r.AuditRetentionDays}, audit_retention_days),
                enforce_mfa_for_critical = COALESCE({r.EnforceMfaForCritical}, enforce_mfa_for_critical),
                updated_at = now()
            WHERE company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
