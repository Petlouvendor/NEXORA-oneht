using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Nexora.Application.Integrations;

namespace Nexora.Infrastructure.Integrations;

/// <summary>Transport substituable des fournisseurs (les tests y branchent un faux serveur).</summary>
public sealed class PaymentProviderHandler(HttpMessageHandler handler)
{
    public HttpMessageHandler Handler { get; } = handler;
}

/// <summary>Client HTTP partagé des fournisseurs de paiement, avec délai d'attente borné.</summary>
public sealed class ProviderHttp
{
    public ProviderHttp(IConfiguration configuration, PaymentProviderHandler? handler = null)
    {
        Client = handler is null ? new HttpClient() : new HttpClient(handler.Handler, disposeHandler: false);
        Client.Timeout = Timeout.InfiniteTimeSpan;   // le délai est porté par chaque appel
        var seconds = int.TryParse(configuration["Payments:TimeoutSeconds"], out var s) ? s : 15;
        RequestTimeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120));
    }

    public HttpClient Client { get; }
    public TimeSpan RequestTimeout { get; }
}

public sealed class PaymentProviderRegistry(IEnumerable<IPaymentProvider> providers) : IPaymentProviderRegistry
{
    public IReadOnlyList<IPaymentProvider> All { get; } = providers.ToList();

    /// <summary>Clé normalisée ; les connecteurs historiques « moncash-sim », « sim »… sont des simulations.</summary>
    public static string Normalize(string? key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        if (k is "simulated" or "simulation" or "sim" || k.EndsWith("-sim", StringComparison.Ordinal) || k.StartsWith("sim-", StringComparison.Ordinal))
            return "simulated";
        return k;
    }

    public IPaymentProvider? Resolve(string? providerKey)
    {
        var k = Normalize(providerKey);
        return All.FirstOrDefault(p => p.Key == k);
    }
}

/// <summary>
/// Fournisseur de SIMULATION : aucun service externe n'est contacté, aucun argent réel ne circule.
/// La confirmation se fait à la main (« simuler la réponse du fournisseur »), seulement en environnement de test.
/// </summary>
public sealed class SimulatedPaymentProvider : IPaymentProvider
{
    public string Key => "simulated";
    public string DisplayName => "Simulation";
    public bool IsSimulation => true;
    public bool SupportsRefund => true;
    public IReadOnlyCollection<string> Currencies => [];

    public Task<ProviderTestResult> TestConnectionAsync(PaymentProviderContext context, CancellationToken ct = default)
        => Task.FromResult(context.IsProduction
            ? new ProviderTestResult(ConnectionStatus.NotConfigured,
                "Un fournisseur de simulation ne peut pas servir en production : choisissez un fournisseur réel.", 0)
            : new ProviderTestResult(ConnectionStatus.Connected,
                "SIMULATION : aucun service externe n'est contacté ; les paiements ne transportent aucun argent réel.", 0));

    public Task<ProviderCreateResult> CreatePaymentAsync(PaymentProviderContext context, ProviderPaymentRequest request, CancellationToken ct = default)
        => Task.FromResult(context.IsProduction
            ? new ProviderCreateResult(false, null, null, ConnectionStatus.NotConfigured, "Simulation interdite en production.")
            : new ProviderCreateResult(true, "SIM-" + request.OrderId, null, null, "Paiement de simulation créé : à confirmer à la main."));

    public Task<ProviderPaymentLookup> RetrievePaymentAsync(PaymentProviderContext context, string orderId, CancellationToken ct = default)
        => Task.FromResult(new ProviderPaymentLookup("not_found", null, null, null,
            "Simulation : aucun fournisseur à interroger ; confirmez ou refusez à la main."));
}

/// <summary>
/// Carte bancaire : aucun acquéreur n'est intégré dans cette version. Le fournisseur existe pour que
/// l'interface le présente honnêtement « non configuré » — il n'appelle rien et ne prétend rien.
/// </summary>
public sealed class CardPaymentProvider : IPaymentProvider
{
    private const string NotAvailable =
        "Aucun acquéreur de cartes n'est intégré à NEXORA ONE : le paiement par carte en ligne n'est pas disponible. " +
        "Les cartes réglées sur un terminal de paiement s'enregistrent en caisse comme moyen « carte ».";

    public string Key => "card";
    public string DisplayName => "Carte bancaire";
    public bool IsSimulation => false;
    public bool SupportsRefund => false;
    public IReadOnlyCollection<string> Currencies => [];

    public Task<ProviderTestResult> TestConnectionAsync(PaymentProviderContext context, CancellationToken ct = default)
        => Task.FromResult(new ProviderTestResult(ConnectionStatus.NotConfigured, NotAvailable, 0));

    public Task<ProviderCreateResult> CreatePaymentAsync(PaymentProviderContext context, ProviderPaymentRequest request, CancellationToken ct = default)
        => Task.FromResult(new ProviderCreateResult(false, null, null, ConnectionStatus.NotConfigured, NotAvailable));

    public Task<ProviderPaymentLookup> RetrievePaymentAsync(PaymentProviderContext context, string orderId, CancellationToken ct = default)
        => Task.FromResult(new ProviderPaymentLookup("error", null, null, null, NotAvailable, ConnectionStatus.NotConfigured));
}

/// <summary>
/// MonCash (Digicel) — API REST publiée par Digicel (« Rest API MonCash Documentation », 2019) :
/// <c>POST {hôte}/oauth/token</c> (Basic client_id:secret, <c>scope=read,write&amp;grant_type=client_credentials</c>),
/// <c>POST /v1/CreatePayment</c> {amount, orderId} → <c>payment_token.token</c>, redirection du client vers
/// <c>{passerelle}/Payment/Redirect?token=…</c>, puis <c>POST /v1/RetrieveOrderPayment</c> {orderId} →
/// <c>payment.{reference, transaction_id, cost, message, payer}</c>. Aucune route de remboursement n'est publiée.
/// Hôtes : <c>sandbox.moncashbutton.digicelgroup.com</c> (test) et <c>moncashbutton.digicelgroup.com</c> (production),
/// remplaçables par la configuration du connecteur (<c>apiBaseUrl</c>, <c>gatewayBaseUrl</c>).
/// </summary>
public sealed class MonCashPaymentProvider(ProviderHttp http) : IPaymentProvider
{
    public const string SandboxApi = "https://sandbox.moncashbutton.digicelgroup.com/Api";
    public const string LiveApi = "https://moncashbutton.digicelgroup.com/Api";
    public const string SandboxGateway = "https://sandbox.moncashbutton.digicelgroup.com/Moncash-middleware";
    public const string LiveGateway = "https://moncashbutton.digicelgroup.com/Moncash-middleware";

    public string Key => "moncash";
    public string DisplayName => "MonCash";
    public bool IsSimulation => false;
    public bool SupportsRefund => false;
    public IReadOnlyCollection<string> Currencies => ["HTG"];

    private static string ConfigString(PaymentProviderContext c, string name)
        => c.Config.ValueKind == JsonValueKind.Object && c.Config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!.TrimEnd('/') : "";

    private static string ApiBase(PaymentProviderContext c)
        => ConfigString(c, "apiBaseUrl") is { Length: > 0 } u ? u : c.IsProduction ? LiveApi : SandboxApi;

    private static string GatewayBase(PaymentProviderContext c)
        => ConfigString(c, "gatewayBaseUrl") is { Length: > 0 } u ? u : c.IsProduction ? LiveGateway : SandboxGateway;

    private const string NotConfiguredMessage =
        "Identifiants MonCash absents : saisissez le Client ID et le Client Secret générés dans le portail MonCash Business.";

    public async Task<ProviderTestResult> TestConnectionAsync(PaymentProviderContext context, CancellationToken ct = default)
    {
        if (!context.HasCredentials) return new ProviderTestResult(ConnectionStatus.NotConfigured, NotConfiguredMessage, 0);
        var sw = Stopwatch.StartNew();
        var token = await TokenAsync(context, ct);
        var ms = (int)sw.ElapsedMilliseconds;
        return token.Token is not null
            ? new ProviderTestResult(ConnectionStatus.Connected,
                $"MonCash {(context.IsProduction ? "production" : "bac à sable")} : authentification réussie.", ms)
            : new ProviderTestResult(token.Status!, token.Message!, ms);
    }

    public async Task<ProviderCreateResult> CreatePaymentAsync(PaymentProviderContext context, ProviderPaymentRequest request, CancellationToken ct = default)
    {
        if (!context.HasCredentials) return new ProviderCreateResult(false, null, null, ConnectionStatus.NotConfigured, NotConfiguredMessage);
        if (!string.Equals(request.Currency, "HTG", StringComparison.OrdinalIgnoreCase))
            return new ProviderCreateResult(false, null, null, null, "MonCash est un portefeuille en gourdes : le montant doit être en HTG.");
        var token = await TokenAsync(context, ct);
        if (token.Token is null) return new ProviderCreateResult(false, null, null, token.Status, token.Message!);

        var body = JsonSerializer.Serialize(new { amount = request.Amount, orderId = request.OrderId });
        var (status, json, error) = await PostJsonAsync(context, "/v1/CreatePayment", token.Token, body, ct);
        if (error is not null) return new ProviderCreateResult(false, null, null, error.Value.Status, error.Value.Message);
        if (status is not (200 or 201 or 202))
            return new ProviderCreateResult(false, null, null, ConnectionStatus.Unavailable, $"MonCash a refusé la création du paiement (HTTP {status}).", json);
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var paymentToken = doc.RootElement.GetProperty("payment_token").GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(paymentToken))
                return new ProviderCreateResult(false, null, null, ConnectionStatus.Unavailable, "Réponse MonCash sans jeton de paiement.", json);
            return new ProviderCreateResult(true, request.OrderId,
                $"{GatewayBase(context)}/Payment/Redirect?token={Uri.EscapeDataString(paymentToken)}", null,
                "Paiement MonCash créé : le client doit le valider sur la page MonCash.", json);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new ProviderCreateResult(false, null, null, ConnectionStatus.Unavailable, "Réponse MonCash illisible.", json);
        }
    }

    public async Task<ProviderPaymentLookup> RetrievePaymentAsync(PaymentProviderContext context, string orderId, CancellationToken ct = default)
    {
        if (!context.HasCredentials) return new ProviderPaymentLookup("error", null, null, null, NotConfiguredMessage, ConnectionStatus.NotConfigured);
        var token = await TokenAsync(context, ct);
        if (token.Token is null) return new ProviderPaymentLookup("error", null, null, null, token.Message!, token.Status);

        var (status, json, error) = await PostJsonAsync(context, "/v1/RetrieveOrderPayment", token.Token,
            JsonSerializer.Serialize(new { orderId }), ct);
        if (error is not null) return new ProviderPaymentLookup("error", null, null, null, error.Value.Message, error.Value.Status);
        if (status == 404)
            return new ProviderPaymentLookup("not_found", null, null, null, "MonCash ne connaît encore aucun paiement pour cette commande.");
        if (status != 200)
            return new ProviderPaymentLookup("error", null, null, null, $"MonCash : réponse HTTP {status}.", ConnectionStatus.Unavailable);
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var p = doc.RootElement.GetProperty("payment");
            var message = p.TryGetProperty("message", out var m) ? m.GetString() : null;
            var cost = p.TryGetProperty("cost", out var c) ? ReadDecimal(c) : null;
            var transaction = p.TryGetProperty("transaction_id", out var t) ? ReadString(t) : null;
            var payer = p.TryGetProperty("payer", out var py) ? ReadString(py) : null;
            return string.Equals(message, "successful", StringComparison.OrdinalIgnoreCase)
                ? new ProviderPaymentLookup("paid", cost, transaction, payer, "Paiement MonCash réussi.")
                : new ProviderPaymentLookup("not_found", cost, transaction, payer, $"MonCash : paiement « {message ?? "inconnu"} ».");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new ProviderPaymentLookup("error", null, null, null, "Réponse MonCash illisible.", ConnectionStatus.Unavailable);
        }
    }

    private static decimal? ReadDecimal(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static string? ReadString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        _ => null,
    };

    private sealed record TokenResult(string? Token, string? Status, string? Message);

    /// <summary>Jeton OAuth (valable 59 s selon la documentation) : demandé à chaque opération, jamais conservé.</summary>
    private async Task<TokenResult> TokenAsync(PaymentProviderContext context, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase(context) + "/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["scope"] = "read,write",
                ["grant_type"] = "client_credentials",
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{context.ClientId}:{context.ClientSecret}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var (status, json, error) = await SendAsync(req, ct);
        if (error is not null) return new TokenResult(null, error.Value.Status, error.Value.Message);
        if (status is 400 or 401 or 403)
            return new TokenResult(null, ConnectionStatus.AuthenticationFailed,
                "MonCash a refusé les identifiants : vérifiez le Client ID et le Client Secret (et l'environnement test / production).");
        if (status != 200) return new TokenResult(null, ConnectionStatus.Unavailable, $"MonCash indisponible (HTTP {status}).");
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var token = doc.RootElement.GetProperty("access_token").GetString();
            return string.IsNullOrWhiteSpace(token)
                ? new TokenResult(null, ConnectionStatus.AuthenticationFailed, "MonCash n'a pas délivré de jeton d'accès.")
                : new TokenResult(token, null, null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new TokenResult(null, ConnectionStatus.Unavailable, "Réponse d'authentification MonCash illisible.");
        }
    }

    private async Task<(int Status, string? Json, (string Status, string Message)? Error)> PostJsonAsync(
        PaymentProviderContext context, string path, string token, string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase(context) + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await SendAsync(req, ct);
    }

    private async Task<(int Status, string? Json, (string Status, string Message)? Error)> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(http.RequestTimeout);
        try
        {
            using var resp = await http.Client.SendAsync(req, cts.Token);
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            if (resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                return ((int)resp.StatusCode, json, (ConnectionStatus.Unavailable, $"MonCash indisponible (HTTP {(int)resp.StatusCode})."));
            return ((int)resp.StatusCode, json, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (0, null, (ConnectionStatus.Timeout, $"MonCash n'a pas répondu en {http.RequestTimeout.TotalSeconds:0} secondes."));
        }
        catch (HttpRequestException e)
        {
            return (0, null, (ConnectionStatus.Unavailable, $"MonCash injoignable : {e.Message}"));
        }
    }
}
