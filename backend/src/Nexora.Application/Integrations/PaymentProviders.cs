using System.Text.Json;

namespace Nexora.Application.Integrations;

/// <summary>
/// Fournisseur de paiement (finalisation phase 15). Le cœur métier ne connaît que ce contrat.
/// Un fournisseur de simulation n'appelle aucun service externe et le dit toujours ; un fournisseur
/// réel n'est jamais présenté comme fonctionnel tant que ses identifiants ne sont pas configurés.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Clé enregistrée sur le connecteur : <c>simulated</c>, <c>moncash</c>, <c>card</c>.</summary>
    string Key { get; }
    string DisplayName { get; }
    /// <summary>Vrai : aucun argent réel ne circule (affiché « SIMULATION »).</summary>
    bool IsSimulation { get; }
    /// <summary>Le fournisseur sait-il rembourser par son API ?</summary>
    bool SupportsRefund { get; }
    /// <summary>Devises acceptées (vide = toutes).</summary>
    IReadOnlyCollection<string> Currencies { get; }

    Task<ProviderTestResult> TestConnectionAsync(PaymentProviderContext context, CancellationToken ct = default);
    Task<ProviderCreateResult> CreatePaymentAsync(PaymentProviderContext context, ProviderPaymentRequest request, CancellationToken ct = default);
    /// <summary>État du paiement chez le fournisseur, retrouvé par le numéro de commande transmis à la création.</summary>
    Task<ProviderPaymentLookup> RetrievePaymentAsync(PaymentProviderContext context, string orderId, CancellationToken ct = default);
}

/// <summary>Résolution du fournisseur d'un connecteur (clé normalisée, alias historiques).</summary>
public interface IPaymentProviderRegistry
{
    IPaymentProvider? Resolve(string? providerKey);
    IReadOnlyList<IPaymentProvider> All { get; }
}

/// <summary>Ce que le connecteur transmet au fournisseur : environnement, identifiants déchiffrés, configuration.</summary>
public sealed record PaymentProviderContext(string Environment, string? ClientId, string? ClientSecret, JsonElement Config)
{
    public bool IsProduction => Environment == "production";
    public bool HasCredentials => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>Statuts imposés (plan §33) — jamais « OK » par défaut.</summary>
public static class ConnectionStatus
{
    public const string Connected = "CONNECTED";
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string Timeout = "TIMEOUT";
    public const string Unavailable = "UNAVAILABLE";
    public const string NotConfigured = "NOT_CONFIGURED";
}

public sealed record ProviderTestResult(string Status, string Message, int LatencyMs);

public sealed record ProviderPaymentRequest(string OrderId, decimal Amount, string Currency, string? Description);

/// <summary>Résultat de création : en cas d'échec, <c>ErrorStatus</c> reprend un statut de connexion.</summary>
public sealed record ProviderCreateResult(
    bool Success, string? ProviderReference, string? RedirectUrl, string? ErrorStatus, string Message, string? RawResponse = null);

/// <summary>
/// État chez le fournisseur : <c>paid</c> (payé, montant et transaction connus), <c>not_found</c>
/// (aucun paiement pour cette commande), <c>error</c> (fournisseur injoignable, refus…).
/// </summary>
public sealed record ProviderPaymentLookup(
    string State, decimal? Amount, string? TransactionId, string? Payer, string Message, string? ErrorStatus = null);
