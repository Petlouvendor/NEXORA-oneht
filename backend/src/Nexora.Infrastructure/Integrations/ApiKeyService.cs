using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Integrations;
using Nexora.Contracts.Integrations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Integrations;

/// <summary>
/// Clés d'API d'entreprise (Phase 35 §13-14). Le secret complet — <c>nxk_{env}_{prefix}_{random}</c>
/// — n'existe qu'en mémoire le temps de la réponse de création ; en base, seul son SHA-256 est
/// stocké. Le périmètre d'une clé est borné aux permissions de son propriétaire.
/// </summary>
public sealed class SqlApiKeyService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IPermissionService permissions, IDateTimeProvider clock)
    : IApiKeyService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private Guid CurrentUser => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    internal static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    internal sealed record Row(Guid Id, string Name, string KeyPrefix, string Environment, string Scopes,
        int RateLimitPerMinute, Guid OwnerUserId, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt,
        string? LastUsedIp, DateTimeOffset? RevokedAt, DateTimeOffset CreatedAt, Guid? AppInstallationId);

    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", name AS "Name", key_prefix AS "KeyPrefix", environment AS "Environment",
                   scopes::text AS "Scopes", rate_limit_per_minute AS "RateLimitPerMinute", owner_user_id AS "OwnerUserId",
                   expires_at AS "ExpiresAt", last_used_at AS "LastUsedAt", host(last_used_ip) AS "LastUsedIp",
                   revoked_at AS "RevokedAt", created_at AS "CreatedAt", app_installation_id AS "AppInstallationId"
            FROM nexora.api_key WHERE company_id = {Company} ORDER BY created_at DESC
            """).ToListAsync(ct);
        return rows.Select(r => ToDto(r, clock.UtcNow)).ToList();
    }

    public async Task<Result<ApiKeyCreatedDto>> CreateAsync(CreateApiKeyRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length is < 2 or > 80)
            return Error.Validation("api_key.name", "Nom invalide (2–80 caractères).");
        var env = request.Environment?.Trim().ToLowerInvariant() ?? "live";
        if (env is not ("test" or "live")) return Error.Validation("api_key.environment", "Environnement invalide (test/live).");
        if (request.RateLimitPerMinute is < 1 or > 100_000)
            return Error.Validation("api_key.rate_limit", "Quota par minute hors bornes.");

        var scopes = (request.Scopes ?? []).Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0).Distinct().ToArray();
        if (scopes.Length == 0) return Error.Validation("api_key.scopes", "Au moins un scope de permission est requis.");

        // §13 : une clé n'accorde jamais plus que ce que son propriétaire détient.
        var profile = await permissions.LoadProfileAsync(CurrentUser, ct);
        var owned = profile.PermissionsFor(Company).ToHashSet(StringComparer.Ordinal);
        var excess = scopes.Where(s => !owned.Contains(s)).ToArray();
        if (excess.Length > 0)
            return Error.Forbidden("api_key.scope_excess", $"Scopes hors de vos permissions : {string.Join(", ", excess)}.");

        // Phase 46 §8, §46 : une clé émise pour une app marketplace installée ne peut pas non plus
        // dépasser le périmètre que l'entreprise a explicitement accordé à cette app.
        if (request.AppInstallationId is { } installationId)
        {
            var granted = await db.Database.SqlQuery<string>($"""
                SELECT granted_scopes::text AS "Value" FROM nexora.app_installation
                WHERE id = {installationId} AND company_id = {Company} AND status = 'installed'
                """).FirstOrDefaultAsync(ct);
            if (granted is null) return Error.NotFound("api_key.app_installation_not_found", "Installation d'application introuvable ou inactive.");
            var grantedSet = JsonSerializer.Deserialize<HashSet<string>>(granted) ?? [];
            var appExcess = scopes.Where(s => !grantedSet.Contains(s)).ToArray();
            if (appExcess.Length > 0)
                return Error.Forbidden("api_key.scope_excess_app", $"Scopes hors du périmètre accordé à l'application : {string.Join(", ", appExcess)}.");
        }

        var (prefix, secret) = NewSecret(env);
        DateTimeOffset? expires = request.ExpiresInDays is { } d and > 0 ? clock.UtcNow.AddDays(d) : null;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.api_key
                (id, company_id, name, key_prefix, key_hash, environment, scopes, rate_limit_per_minute,
                 owner_user_id, expires_at, created_by, app_installation_id)
            VALUES ({id}, {Company}, {request.Name.Trim()}, {prefix}, {Hash(secret)}, {env},
                    {JsonSerializer.Serialize(scopes)}::jsonb, {request.RateLimitPerMinute}, {CurrentUser}, {expires}, {CurrentUser},
                    {request.AppInstallationId})
            """, ct);
        await uow.SaveChangesAsync(ct);

        return Result.Success(new ApiKeyCreatedDto(await GetAsync(id, ct), secret));
    }

    public async Task<Result<ApiKeyCreatedDto>> RotateAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await LoadAsync(id, ct);
        if (existing is null) return Error.NotFound("api_key.not_found", "Clé introuvable.");
        if (existing.RevokedAt is not null) return Error.Conflict("api_key.revoked", "Clé déjà révoquée.");

        var scopes = JsonSerializer.Deserialize<string[]>(existing.Scopes) ?? [];
        var (prefix, secret) = NewSecret(existing.Environment);
        var newId = Guid.NewGuid();

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.api_key SET revoked_at = now(), revoked_by = {CurrentUser}, updated_at = now() WHERE id = {id}
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.api_key
                    (id, company_id, name, key_prefix, key_hash, environment, scopes, rate_limit_per_minute,
                     owner_user_id, expires_at, rotated_from, created_by, app_installation_id)
                VALUES ({newId}, {Company}, {existing.Name}, {prefix}, {Hash(secret)}, {existing.Environment},
                        {JsonSerializer.Serialize(scopes)}::jsonb, {existing.RateLimitPerMinute}, {existing.OwnerUserId},
                        {existing.ExpiresAt}, {id}, {CurrentUser}, {existing.AppInstallationId})
                """, token);
            return 0;
        }, ct);

        return Result.Success(new ApiKeyCreatedDto(await GetAsync(newId, ct), secret));
    }

    public async Task<Result> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.api_key SET revoked_at = now(), revoked_by = {CurrentUser}, updated_at = now()
            WHERE id = {id} AND company_id = {Company} AND revoked_at IS NULL
            """, ct);
        return n == 0 ? Error.NotFound("api_key.not_found", "Clé introuvable ou déjà révoquée.") : Result.Success();
    }

    private async Task<Row?> LoadAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", name AS "Name", key_prefix AS "KeyPrefix", environment AS "Environment",
                   scopes::text AS "Scopes", rate_limit_per_minute AS "RateLimitPerMinute", owner_user_id AS "OwnerUserId",
                   expires_at AS "ExpiresAt", last_used_at AS "LastUsedAt", host(last_used_ip) AS "LastUsedIp",
                   revoked_at AS "RevokedAt", created_at AS "CreatedAt", app_installation_id AS "AppInstallationId"
            FROM nexora.api_key WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    private async Task<ApiKeyDto> GetAsync(Guid id, CancellationToken ct)
        => ToDto((await LoadAsync(id, ct))!, clock.UtcNow);

    private static (string Prefix, string Secret) NewSecret(string env)
    {
        var pub = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var priv = Base62(RandomNumberGenerator.GetBytes(24));
        var prefix = $"nxk_{env}_{pub}";
        return (prefix, $"{prefix}_{priv}");
    }

    private static readonly char[] B62 = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
    private static string Base62(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes) sb.Append(B62[b % 62]);
        return sb.ToString();
    }

    private static ApiKeyDto ToDto(SqlApiKeyService.Row r, DateTimeOffset now)
    {
        var status = r.RevokedAt is not null ? "revoked"
            : r.ExpiresAt is { } e && e <= now ? "expired"
            : "active";
        return new ApiKeyDto(r.Id, r.Name, r.KeyPrefix, r.Environment,
            JsonSerializer.Deserialize<List<string>>(r.Scopes) ?? [], r.RateLimitPerMinute, r.OwnerUserId,
            r.ExpiresAt, r.LastUsedAt, r.LastUsedIp, r.RevokedAt, r.CreatedAt, status, r.AppInstallationId);
    }
}

// -----------------------------------------------------------------------------

/// <summary>
/// Résolution d'une clé d'API présentée en en-tête (Phase 35 §13). Portée système : la requête
/// n'a pas encore de contexte tenant. Recherche par préfixe visible puis vérification de
/// l'empreinte à temps constant.
/// </summary>
public sealed class SqlApiKeyAuthenticator(NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock)
    : IApiKeyAuthenticator
{
    private sealed record AuthRow(Guid Id, Guid CompanyId, Guid OwnerUserId, string KeyHash, string Scopes,
        int RateLimitPerMinute, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt);

    public async Task<ApiKeyResolution> ResolveAsync(string presentedKey, string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presentedKey) || !presentedKey.StartsWith("nxk_", StringComparison.Ordinal))
            return new ApiKeyResolution(ApiKeyOutcome.Invalid);

        // « nxk_live_ab12cd34_<secret> » → préfixe = les 3 premiers segments.
        var parts = presentedKey.Split('_');
        if (parts.Length < 4) return new ApiKeyResolution(ApiKeyOutcome.Invalid);
        var prefix = string.Join('_', parts[0], parts[1], parts[2]);

        using var _ = tenant.BeginSystemScope();
        var row = await db.Database.SqlQuery<AuthRow>($"""
            SELECT id AS "Id", company_id AS "CompanyId", owner_user_id AS "OwnerUserId", key_hash AS "KeyHash",
                   scopes::text AS "Scopes", rate_limit_per_minute AS "RateLimitPerMinute",
                   expires_at AS "ExpiresAt", revoked_at AS "RevokedAt"
            FROM nexora.api_key WHERE key_prefix = {prefix}
            """).FirstOrDefaultAsync(ct);

        if (row is null) return new ApiKeyResolution(ApiKeyOutcome.Invalid);

        var presentedHash = SqlApiKeyService.Hash(presentedKey);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(presentedHash), Convert.FromHexString(row.KeyHash)))
            return new ApiKeyResolution(ApiKeyOutcome.Invalid);

        if (row.RevokedAt is not null) return new ApiKeyResolution(ApiKeyOutcome.Revoked, row.Id);
        if (row.ExpiresAt is { } e && e <= clock.UtcNow) return new ApiKeyResolution(ApiKeyOutcome.Expired, row.Id);

        // Compte d'usage — best effort, ne bloque pas la requête.
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.api_key SET last_used_at = now(), last_used_ip = {ipAddress}::inet, updated_at = now()
                WHERE id = {row.Id}
                """, ct);
        }
        catch { /* la télémétrie n'empêche jamais l'authentification */ }

        var scopes = System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.Scopes) ?? [];
        return new ApiKeyResolution(ApiKeyOutcome.Ok, row.Id, row.CompanyId, row.OwnerUserId, scopes, row.RateLimitPerMinute);
    }
}
