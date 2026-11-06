using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// Secrets de signature chiffrés au repos par la clé des données (audit 2026-10 éd. 2 : stockés en clair, lisibles par
    /// quiconque lit la base). Un secret enregistré avant ce changement, sans préfixe, reste lu tel quel.
    /// </summary>
    private const string SealedPrefix = "enc:v1:";

    public static string Seal(Payroll.PersonalDataProtector protector, string secret) => SealedPrefix + protector.Protect(secret);

    public static string? Open(Payroll.PersonalDataProtector protector, string? stored)
        => stored is not null && stored.StartsWith(SealedPrefix, StringComparison.Ordinal)
            ? protector.Unprotect(stored[SealedPrefix.Length..])
            : stored;

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

/// <summary>Connecteur vu par un fournisseur : identifiants déchiffrés (jamais exposés hors du serveur).</summary>
internal static class ConnectorContexts
{
    private sealed record CredRow(string Environment, string? Encrypted, string Config);
    private sealed record Creds(string ClientId, string ClientSecret);

    public static async Task<PaymentProviderContext?> LoadAsync(NexoraDbContext db, Payroll.PersonalDataProtector protector,
        Guid connectorId, Guid companyId, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<CredRow>($"""
            SELECT environment AS "Environment", credentials_encrypted AS "Encrypted", config::text AS "Config"
            FROM nexora.integration_connector WHERE id = {connectorId} AND company_id = {companyId}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        Creds? creds = null;
        if (row.Encrypted is { Length: > 0 } enc && protector.Unprotect(enc) is { } plain)
            creds = JsonSerializer.Deserialize<Creds>(plain);
        return new PaymentProviderContext(row.Environment, creds?.ClientId, creds?.ClientSecret,
            JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(row.Config) ? "{}" : row.Config));
    }

    public static string Protect(Payroll.PersonalDataProtector protector, string clientId, string clientSecret)
        => protector.Protect(JsonSerializer.Serialize(new Creds(clientId, clientSecret)))!;
}

/// <summary>
/// Registre des intégrations (§5-6) : un connecteur par (entreprise, code), chaque entreprise
/// avec ses propres fournisseurs et secrets même au sein d'un groupe. Les secrets ne sont
/// jamais renvoyés en lecture — seulement à la rotation (§38). Finalisation phase 15 : le test de
/// connexion interroge réellement le fournisseur ; un fournisseur de simulation le dit toujours.
/// </summary>
public sealed class SqlIntegrationRegistry(
    NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant, IPaymentProviderRegistry providers,
    Payroll.PersonalDataProtector protector) : IIntegrationRegistry
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private static readonly string[] Types =
        ["payment", "bank", "ecommerce", "marketplace", "messaging", "email_sms",
         "document_storage", "accounting_export", "pos_terminal", "other"];
    private static readonly string[] Statuses = ["inactive", "active", "error"];

    private sealed record Row(
        Guid Id, string Code, string Name, string ConnectorType, string Provider, string Environment,
        string Status, DateTimeOffset? LastSyncAt, string? LastError, DateTimeOffset? LastErrorAt,
        string ConnectorVersion, bool HasSecret, string Config, DateTimeOffset UpdatedAt,
        bool CredentialsConfigured, string? CredentialsHint, string? LastTestStatus, string? LastTestMessage, DateTimeOffset? LastTestedAt);

    private IQueryable<Row> Query(Guid? id) => db.Database.SqlQuery<Row>($"""
        SELECT id AS "Id", code AS "Code", name AS "Name", connector_type AS "ConnectorType",
               provider AS "Provider", environment AS "Environment", status AS "Status",
               last_sync_at AS "LastSyncAt", last_error AS "LastError", last_error_at AS "LastErrorAt",
               connector_version AS "ConnectorVersion", (webhook_secret IS NOT NULL) AS "HasSecret",
               config::text AS "Config", updated_at AS "UpdatedAt",
               (credentials_encrypted IS NOT NULL) AS "CredentialsConfigured", credentials_hint AS "CredentialsHint",
               last_test_status AS "LastTestStatus", last_test_message AS "LastTestMessage", last_tested_at AS "LastTestedAt"
        FROM nexora.integration_connector
        WHERE company_id = {Company} AND ({id}::uuid IS NULL OR id = {id})
        ORDER BY connector_type, name
        """);

    public async Task<IReadOnlyList<ConnectorDto>> ListAsync(CancellationToken ct = default)
        => (await Query(null).ToListAsync(ct)).Select(Map).ToList();

    public async Task<Result<ConnectorDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await Query(id).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("integrations.connector", "Connecteur introuvable.") : Result.Success(Map(row));
    }

    public IReadOnlyList<PaymentProviderInfo> Providers()
        => providers.All.Select(p => new PaymentProviderInfo(p.Key, p.DisplayName, p.IsSimulation, p.SupportsRefund, p.Currencies)).ToList();

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
        var provider = request.Provider.Trim();
        if (type == "payment")
        {
            // Un connecteur de paiement désigne un fournisseur réellement disponible — jamais un nom libre.
            var p = providers.Resolve(provider);
            if (p is null)
                return Error.Validation("integrations.provider_unknown",
                    $"Fournisseur de paiement inconnu. Disponibles : {string.Join(", ", providers.All.Select(x => x.Key))}.");
            provider = p.Key;
            if (p.IsSimulation && env == "production")
                return Error.Validation("integrations.simulation_in_production",
                    "Un fournisseur de simulation ne peut pas être utilisé en production.");
        }
        var config = request.Config?.GetRawText() ?? "{}";

        return await uow.ExecuteInTransactionAsync(async token =>
        {
            var existing = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM nexora.integration_connector
                WHERE company_id = {Company} AND code = {code}
                """).FirstOrDefaultAsync(token);

            if (existing != Guid.Empty)
            {
                // Changer de fournisseur ou d'environnement invalide le dernier test : le connecteur repasse
                // inactif jusqu'à un nouveau test réussi (pas de bascule silencieuse vers la production).
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.integration_connector
                    SET status = CASE WHEN provider <> {provider} OR environment <> {env} THEN 'inactive' ELSE status END,
                        last_test_status = CASE WHEN provider <> {provider} OR environment <> {env} THEN NULL ELSE last_test_status END,
                        name = {request.Name.Trim()}, connector_type = {type}, provider = {provider},
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
                VALUES ({id}, {Company}, {code}, {request.Name.Trim()}, {type}, {provider}, {env},
                        'inactive', {config}::jsonb, {IntegrationCrypto.Seal(protector, IntegrationCrypto.NewSecret())},
                        {"vault://" + Company + "/" + code}, {tenant.UserId})
                """, token);
            return Result.Success(id);
        }, ct);
    }

    public async Task<Result> SetStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        var s = status?.Trim().ToLowerInvariant() ?? "";
        if (!Statuses.Contains(s)) return Error.Validation("integrations.status", $"Statut invalide ({string.Join('/', Statuses)}).");
        var row = await Query(id).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("integrations.connector", "Connecteur introuvable.");
        if (s == "active" && row.ConnectorType == "payment")
        {
            var p = providers.Resolve(row.Provider);
            if (p is null) return Error.Validation("integrations.provider_unknown", "Fournisseur de paiement inconnu : connecteur non activable.");
            if (p.IsSimulation && row.Environment == "production")
                return Error.Validation("integrations.simulation_in_production", "Un fournisseur de simulation ne peut pas être utilisé en production.");
            // Un fournisseur réel ne s'active qu'après un test de connexion réussi.
            if (!p.IsSimulation && row.LastTestStatus != ConnectionStatus.Connected)
                return Error.Conflict("integrations.not_tested",
                    $"Testez d'abord la connexion avec succès : dernier résultat « {StatusLabel(row.LastTestStatus)} ».");
        }
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.integration_connector SET status = {s},
                last_error = CASE WHEN {s} = 'error' THEN last_error ELSE NULL END, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        return Result.Success();
    }

    public async Task<Result<ConnectorTestResult>> TestConnectionAsync(Guid id, CancellationToken ct = default)
    {
        var row = await Query(id).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("integrations.connector", "Connecteur introuvable.");

        ProviderTestResult result;
        var mode = "real";
        var provider = row.ConnectorType == "payment" ? providers.Resolve(row.Provider) : null;
        if (provider is null)
        {
            // Aucun adaptateur : on le dit, au lieu de répondre « OK ».
            result = new ProviderTestResult(ConnectionStatus.NotConfigured,
                $"Aucun adaptateur n'est intégré pour « {row.Provider} » ({row.ConnectorType}) : ce connecteur ne peut pas encore être testé.", 0);
        }
        else
        {
            mode = provider.IsSimulation ? "simulation" : "real";
            var context = await ConnectorContexts.LoadAsync(db, protector, id, Company, ct);
            result = await provider.TestConnectionAsync(context!, ct);
        }

        var ok = result.Status == ConnectionStatus.Connected;
        // Un connecteur actif qui ne répond plus passe en erreur ; un connecteur en erreur qui répond redevient actif.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.integration_connector
            SET last_test_status = {result.Status}, last_test_message = {result.Message}, last_tested_at = now(),
                last_sync_at = CASE WHEN {ok} THEN now() ELSE last_sync_at END,
                last_error = CASE WHEN {ok} THEN NULL ELSE {result.Message} END,
                last_error_at = CASE WHEN {ok} THEN NULL ELSE now() END,
                status = CASE WHEN {ok} AND status = 'error' THEN 'active'
                              WHEN NOT {ok} AND status = 'active' THEN 'error' ELSE status END,
                updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        return Result.Success(new ConnectorTestResult(ok, result.Message, result.LatencyMs, result.Status, mode));
    }

    public async Task<Result> SetCredentialsAsync(Guid id, SetConnectorCredentialsRequest request, CancellationToken ct = default)
    {
        var row = await Query(id).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("integrations.connector", "Connecteur introuvable.");
        var provider = row.ConnectorType == "payment" ? providers.Resolve(row.Provider) : null;
        if (provider is null || provider.IsSimulation || provider.Key == "card")
            return Error.Validation("integrations.credentials_unused", "Ce fournisseur n'utilise pas d'identifiants.");
        var clientId = request.ClientId?.Trim() ?? "";
        var secret = request.ClientSecret?.Trim() ?? "";
        if (clientId.Length is < 4 or > 200) return Error.Validation("integrations.client_id", "Client ID invalide.");
        if (secret.Length is < 4 or > 500) return Error.Validation("integrations.client_secret", "Client Secret invalide.");

        var hint = clientId.Length <= 6 ? new string('•', clientId.Length) : $"{clientId[..4]}…{clientId[^2..]}";
        // Nouveaux identifiants : l'ancien test ne vaut plus, le connecteur repasse inactif jusqu'au prochain test réussi.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.integration_connector
            SET credentials_encrypted = {ConnectorContexts.Protect(protector, clientId, secret)}, credentials_hint = {hint},
                last_test_status = NULL, last_test_message = NULL,
                status = CASE WHEN status = 'active' THEN 'inactive' ELSE status END,
                configured_by = {tenant.UserId}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        return Result.Success();
    }

    public async Task<Result<RotateSecretResult>> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        var secret = IntegrationCrypto.NewSecret();
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.integration_connector SET webhook_secret = {IntegrationCrypto.Seal(protector, secret)}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        return n == 0
            ? Error.NotFound("integrations.connector", "Connecteur introuvable.")
            : Result.Success(new RotateSecretResult(secret));
    }

    private static string StatusLabel(string? status) => status switch
    {
        ConnectionStatus.Connected => "connecté",
        ConnectionStatus.AuthenticationFailed => "authentification refusée",
        ConnectionStatus.Timeout => "délai dépassé",
        ConnectionStatus.Unavailable => "service indisponible",
        ConnectionStatus.NotConfigured => "non configuré",
        _ => "jamais testé",
    };

        private ConnectorDto Map(Row r)
    {
        var provider = r.ConnectorType == "payment" ? providers.Resolve(r.Provider) : null;
        return new ConnectorDto(
            r.Id, r.Code, r.Name, r.ConnectorType, r.Provider, r.Environment, r.Status,
            r.LastSyncAt, r.LastError, r.LastErrorAt, r.ConnectorVersion, r.HasSecret,
            JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(r.Config) ? "{}" : r.Config), r.UpdatedAt,
            provider?.IsSimulation == true ? "simulation" : "real", r.CredentialsConfigured, r.CredentialsHint,
            r.LastTestStatus, r.LastTestMessage, r.LastTestedAt, provider is not null);
    }
}

/// <summary>
/// Transport HTTP par défaut d'un webhook sortant (§32). Raccourci bac à sable (§42) pour
/// l'hôte réservé <c>sim.webhook.local</c> : chemin se terminant par <c>/ok</c> → 200, sinon échec.
/// Protection SSRF (analyse 2026-10 §6.5) : URL contrôlée à nouveau, adresse résolue contrôlée à la connexion,
/// redirections non suivies — une cible enregistrée avant la protection est donc bloquée aussi.
/// </summary>
public sealed class HttpOutboundWebhookDispatcher(IConfiguration configuration) : IOutboundWebhookDispatcher
{
    private readonly bool _allowPrivate = configuration.GetValue("Integrations:AllowPrivateWebhookTargets", false);
    private HttpClient? _http;
    private HttpClient Http => _http ??= new HttpClient(WebhookTargetPolicy.CreateHandler(_allowPrivate)) { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<(int? StatusCode, string? Error)> SendAsync(string url, string signature, string body, CancellationToken ct = default)
    {
        if (WebhookTargetPolicy.Validate(url, _allowPrivate) is { } refusal) return (null, refusal);
        var uri = new Uri(url.Trim());

        if (string.Equals(uri.Host, WebhookTargetPolicy.SimulationHost, StringComparison.OrdinalIgnoreCase))
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
            return ((int)resp.StatusCode, resp.IsSuccessStatusCode ? null
                : (int)resp.StatusCode is >= 300 and < 400 ? $"HTTP {(int)resp.StatusCode} : redirection non suivie" : $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
