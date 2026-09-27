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

/// <summary>Utilitaires partagés des connecteurs : secrets, signatures HMAC (§12, §38).</summary>
internal static class IntegrationCrypto
{
    public static string NewSecret()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return "whsec_" + Convert.ToHexString(buf).ToLowerInvariant();
    }

    public static string Sign(string secret, string body)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

    public static bool SignatureMatches(string secret, string body, string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided)) return false;
        var expected = Sign(secret, body);
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided.Trim().ToLowerInvariant());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>
/// Registre des intégrations (§5-6) : un connecteur par (entreprise, code), chaque entreprise
/// avec ses propres fournisseurs et secrets même au sein d'un groupe. Les secrets ne sont
/// jamais renvoyés en lecture — seulement à la rotation (§38).
/// </summary>
public sealed class SqlIntegrationRegistry(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant, IDateTimeProvider clock)
    : IIntegrationRegistry
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private static readonly string[] Types =
        ["payment", "bank", "ecommerce", "marketplace", "messaging", "email_sms",
         "document_storage", "accounting_export", "pos_terminal", "other"];
    private static readonly string[] Statuses = ["inactive", "active", "error"];

    private sealed record Row(
        Guid Id, string Code, string Name, string ConnectorType, string Provider, string Environment,
        string Status, DateTimeOffset? LastSyncAt, string? LastError, DateTimeOffset? LastErrorAt,
        string ConnectorVersion, bool HasSecret, string Config, DateTimeOffset UpdatedAt);

    public async Task<IReadOnlyList<ConnectorDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", connector_type AS "ConnectorType",
                   provider AS "Provider", environment AS "Environment", status AS "Status",
                   last_sync_at AS "LastSyncAt", last_error AS "LastError", last_error_at AS "LastErrorAt",
                   connector_version AS "ConnectorVersion", (webhook_secret IS NOT NULL) AS "HasSecret",
                   config::text AS "Config", updated_at AS "UpdatedAt"
            FROM nexora.integration_connector WHERE company_id = {Company}
            ORDER BY connector_type, name
            """).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<Result<ConnectorDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", connector_type AS "ConnectorType",
                   provider AS "Provider", environment AS "Environment", status AS "Status",
                   last_sync_at AS "LastSyncAt", last_error AS "LastError", last_error_at AS "LastErrorAt",
                   connector_version AS "ConnectorVersion", (webhook_secret IS NOT NULL) AS "HasSecret",
                   config::text AS "Config", updated_at AS "UpdatedAt"
            FROM nexora.integration_connector WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("integrations.connector", "Connecteur introuvable.") : Result.Success(Map(row));
    }

    public async Task<Result<Guid>> UpsertAsync(UpsertConnectorRequest request, CancellationToken ct = default)
    {
        var code = request.Code?.Trim().ToLowerInvariant() ?? "";
        if (code.Length is < 2 or > 60) return Error.Validation("integrations.code", "Code du connecteur invalide (2 à 60 caractères).");
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("integrations.name", "Nom requis.");
        var type = request.ConnectorType?.Trim().ToLowerInvariant() ?? "";
        if (!Types.Contains(type)) return Error.Validation("integrations.type", $"Type inconnu. Attendu : {string.Join(", ", Types)}.");
        if (string.IsNullOrWhiteSpace(request.Provider)) return Error.Validation("integrations.provider", "Fournisseur requis.");
        var env = (request.Environment?.Trim().ToLowerInvariant()) switch
        {
            "production" or "prod" => "production",
            _ => "test",
        };
        var config = request.Config?.GetRawText() ?? "{}";

        return await uow.ExecuteInTransactionAsync(async token =>
        {
            var existing = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM nexora.integration_connector
                WHERE company_id = {Company} AND code = {code}
                """).FirstOrDefaultAsync(token);

            if (existing != Guid.Empty)
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.integration_connector
                    SET name = {request.Name.Trim()}, connector_type = {type}, provider = {request.Provider.Trim()},
                        environment = {env}, config = {config}::jsonb, configured_by = {tenant.UserId}, updated_at = now()
                    WHERE id = {existing}
                    """, token);
                return Result.Success(existing);
            }

            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.integration_connector
                    (id, company_id, code, name, connector_type, provider, environment, status, config,
                     webhook_secret, credentials_ref, configured_by)
                VALUES ({id}, {Company}, {code}, {request.Name.Trim()}, {type}, {request.Provider.Trim()}, {env},
                        'inactive', {config}::jsonb, {IntegrationCrypto.NewSecret()},
                        {"vault://" + Company + "/" + code}, {tenant.UserId})
                """, token);
            return Result.Success(id);
        }, ct);
    }

    public async Task<Result> SetStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        var s = status?.Trim().ToLowerInvariant() ?? "";
        if (!Statuses.Contains(s)) return Error.Validation("integrations.status", $"Statut invalide ({string.Join('/', Statuses)}).");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.integration_connector SET status = {s},
                last_error = CASE WHEN {s} = 'error' THEN last_error ELSE NULL END, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        return n == 0 ? Error.NotFound("integrations.connector", "Connecteur introuvable.") : Result.Success();
    }

    public async Task<Result<ConnectorTestResult>> TestConnectionAsync(Guid id, CancellationToken ct = default)
    {
        var get = await GetAsync(id, ct);
        if (!get.IsSuccess) return get.Error!;

        // Adaptateur simulé (§42) : un vrai connecteur appellerait ici le fournisseur.
        var started = clock.UtcNow;
        await Task.Delay(1, ct);
        var latency = Math.Max(1, (int)(clock.UtcNow - started).TotalMilliseconds) + 12;

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.integration_connector SET last_sync_at = now(), last_error = NULL, last_error_at = NULL, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        return Result.Success(new ConnectorTestResult(true, $"Connecteur « {get.Value!.Provider} » ({get.Value.Environment}) — réponse simulée OK.", latency));
    }

    public async Task<Result<RotateSecretResult>> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        var secret = IntegrationCrypto.NewSecret();
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.integration_connector SET webhook_secret = {secret}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        return n == 0
            ? Error.NotFound("integrations.connector", "Connecteur introuvable.")
            : Result.Success(new RotateSecretResult(secret));
    }

    private static ConnectorDto Map(Row r) => new(
        r.Id, r.Code, r.Name, r.ConnectorType, r.Provider, r.Environment, r.Status,
        r.LastSyncAt, r.LastError, r.LastErrorAt, r.ConnectorVersion, r.HasSecret,
        JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(r.Config) ? "{}" : r.Config), r.UpdatedAt);
}

/// <summary>
/// Transport HTTP par défaut d'un webhook sortant (§32). Raccourci bac à sable (§42) pour
/// l'hôte réservé <c>sim.webhook.local</c> : chemin se terminant par <c>/ok</c> → 200, sinon échec.
/// </summary>
public sealed class HttpOutboundWebhookDispatcher : IOutboundWebhookDispatcher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<(int? StatusCode, string? Error)> SendAsync(string url, string signature, string body, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (null, "URL cible invalide.");

        if (string.Equals(uri.Host, "sim.webhook.local", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.TrimEnd('/').EndsWith("/ok", StringComparison.OrdinalIgnoreCase)
                ? (200, null)
                : (null, "simulation : point de terminaison indisponible");
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Nexora-Signature", signature);
            req.Headers.TryAddWithoutValidation("User-Agent", "Nexora-One-Webhooks/1");
            using var resp = await Http.SendAsync(req, ct);
            return ((int)resp.StatusCode, resp.IsSuccessStatusCode ? null : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
