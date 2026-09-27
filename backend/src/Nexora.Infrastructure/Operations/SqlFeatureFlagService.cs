using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Operations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Operations;

/// <summary>
/// Bascules de fonctionnalité (Phase 41 §27) — table plateforme <c>feature_flag</c>. Résolution par
/// spécificité croissante (utilisateur &gt; entreprise &gt; global), avec un <b>kill switch</b> :
/// une bascule globale explicitement désactivée l'emporte toujours sur un réglage plus spécifique.
/// </summary>
public sealed class SqlFeatureFlagService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IFeatureFlagService
{
    private sealed record FlagRow(Guid Id, string Key, string Scope, Guid? CompanyId, Guid? UserId,
        bool Enabled, string? Description, DateTimeOffset? ExpiresAt, DateTimeOffset UpdatedAt);

    private const string Select = """
        SELECT id AS "Id", key AS "Key", scope AS "Scope", company_id AS "CompanyId", user_id AS "UserId",
               enabled AS "Enabled", description AS "Description", expires_at AS "ExpiresAt", updated_at AS "UpdatedAt"
        FROM nexora.feature_flag
        """;

    public async Task<IReadOnlyList<FeatureFlagDto>> ListAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.SqlQueryRaw<FlagRow>(Select + " ORDER BY key, scope").ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<FeatureFlagDto>> SaveAsync(SaveFeatureFlagRequest request, CancellationToken ct = default)
    {
        var key = request.Key.Trim().ToLowerInvariant();
        if (key.Length is < 2 or > 100) return Error.Validation("flag.key", "Clé invalide (2-100 caractères).");
        var scope = request.Scope.Trim().ToLowerInvariant();
        if (scope is not ("global" or "company" or "user"))
            return Error.Validation("flag.scope", "Portée invalide (global / company / user).");
        if (scope == "company" && request.CompanyId is null)
            return Error.Validation("flag.company_id", "Entreprise requise pour une bascule d'entreprise.");
        if (scope == "user" && request.UserId is null)
            return Error.Validation("flag.user_id", "Utilisateur requis pour une bascule utilisateur.");
        if (scope != "company" && request.CompanyId is not null)
            return Error.Validation("flag.scope_mismatch", "Entreprise fournie pour une portée non-entreprise.");
        if (scope != "user" && request.UserId is not null)
            return Error.Validation("flag.scope_mismatch", "Utilisateur fourni pour une portée non-utilisateur.");

        using var _ = tenant.BeginSystemScope();
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.feature_flag (id, key, scope, company_id, user_id, enabled, description, expires_at, created_by)
            VALUES ({id}, {key}, {scope}, {request.CompanyId}, {request.UserId}, {request.Enabled},
                    {request.Description}, {request.ExpiresAt}, {tenant.UserId})
            ON CONFLICT ON CONSTRAINT feature_flag_uk DO UPDATE SET
                enabled = EXCLUDED.enabled, description = EXCLUDED.description,
                expires_at = EXCLUDED.expires_at, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var saved = await db.Database.SqlQueryRaw<FlagRow>(
            Select + " WHERE key = {0} AND scope = {1} AND company_id IS NOT DISTINCT FROM {2} AND user_id IS NOT DISTINCT FROM {3}",
            key, scope, (object?)request.CompanyId ?? DBNull.Value, (object?)request.UserId ?? DBNull.Value).FirstAsync(ct);
        return Result.Success(ToDto(saved));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.feature_flag WHERE id = {id}", ct);
        if (rows == 0) return Error.NotFound("flag.not_found", "Bascule introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<bool> IsEnabledAsync(string key, Guid? companyId, Guid? userId, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var now = DateTimeOffset.UtcNow;
        var rows = await db.Database.SqlQueryRaw<FlagRow>(Select + " WHERE key = {0}", key.Trim().ToLowerInvariant()).ToListAsync(ct);
        bool NotExpired(FlagRow r) => r.ExpiresAt is null || r.ExpiresAt > now;

        var global = rows.FirstOrDefault(r => r.Scope == "global" && NotExpired(r));
        if (global is { Enabled: false }) return false;   // kill switch : l'emporte toujours (§27)

        if (userId is { } u)
        {
            var userRow = rows.FirstOrDefault(r => r.Scope == "user" && r.UserId == u && NotExpired(r));
            if (userRow is not null) return userRow.Enabled;
        }
        if (companyId is { } c)
        {
            var companyRow = rows.FirstOrDefault(r => r.Scope == "company" && r.CompanyId == c && NotExpired(r));
            if (companyRow is not null) return companyRow.Enabled;
        }
        return global?.Enabled ?? false;
    }

    private static FeatureFlagDto ToDto(FlagRow r) =>
        new(r.Id, r.Key, r.Scope, r.CompanyId, r.UserId, r.Enabled, r.Description, r.ExpiresAt, r.UpdatedAt);
}
