using Microsoft.Extensions.Configuration;

namespace Nexora.Infrastructure.Operations;

/// <summary>Un contrôle de configuration de production : <c>pass</c>, <c>warn</c> ou <c>fail</c>.</summary>
public sealed record ConfigurationCheck(string Key, string Label, string Category, string Status, string Detail);

/// <summary>
/// Configuration de production (finalisation phase 18, §39 « configuration production, gestion secrets, HTTPS ») :
/// une seule lecture de la configuration, partagée par le garde de démarrage (secrets), la checklist de mise en
/// production et la vue « Production » de l'exploitation. Ne rend jamais la valeur d'un secret.
/// </summary>
public static class ProductionConfiguration
{
    private static readonly string[] WeakMarkers = ["dev-only", "insecure", "change-me", "changeme", "change_me"];

    /// <summary>Secrets manquants ou faibles : l'API refuse de démarrer hors développement s'il y en a.</summary>
    public static IReadOnlyList<string> SecretProblems(IConfiguration configuration)
    {
        var problems = new List<string>();

        var jwtKey = configuration["Jwt:SigningKey"] ?? "";
        if (string.IsNullOrWhiteSpace(jwtKey))
            problems.Add("Jwt:SigningKey absente");
        else if (jwtKey.Length < 32 || WeakMarkers.Any(m => jwtKey.Contains(m, StringComparison.OrdinalIgnoreCase)))
            problems.Add("Jwt:SigningKey trop courte (< 32 caractères) ou de développement");

        var mfaKey = configuration["Security:MfaEncryptionKey"] ?? "";
        if (string.IsNullOrWhiteSpace(mfaKey))
            problems.Add("Security:MfaEncryptionKey absente");
        else if (Base64KeyProblem(mfaKey) is { } mfaProblem)
            problems.Add("Security:MfaEncryptionKey " + mfaProblem);

        // Clé des données personnelles (paie, identifiants de paiement) : facultative — elle retombe sur la clé MFA —
        // mais si elle est fournie, elle doit être valable (sinon chaque chiffrement échouerait en pleine opération).
        var dataKey = configuration["Security:DataEncryptionKey"];
        if (!string.IsNullOrWhiteSpace(dataKey) && Base64KeyProblem(dataKey) is { } dataProblem)
            problems.Add("Security:DataEncryptionKey " + dataProblem);

        // Facturation plateforme (A-049) : facultative — sans jeton, le webhook est fermé — mais un jeton fourni doit
        // être fort ; l'ancien jeton de développement, publié dans le code, est refusé.
        var webhookToken = configuration["PlatformBilling:WebhookToken"];
        if (!string.IsNullOrWhiteSpace(webhookToken)
            && (webhookToken.Length < 32 || webhookToken.StartsWith("dev-", StringComparison.OrdinalIgnoreCase)
                || WeakMarkers.Any(m => webhookToken.Contains(m, StringComparison.OrdinalIgnoreCase))))
            problems.Add("PlatformBilling:WebhookToken trop court (< 32 caractères) ou de développement");

        var connection = configuration["Database:ConnectionString"] ?? "";
        if (string.IsNullOrWhiteSpace(connection))
            problems.Add("Database:ConnectionString absente");
        else if (connection.Contains("change_me_in_deployment", StringComparison.OrdinalIgnoreCase)
                 || connection.Contains("Password=nexora_app_dev", StringComparison.OrdinalIgnoreCase))
            problems.Add("Database:ConnectionString utilise le mot de passe par défaut du rôle applicatif");

        return problems;
    }

    private static string? Base64KeyProblem(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == 32 ? null : "doit décoder en 32 octets (base64)";
        }
        catch (FormatException)
        {
            return "n'est pas du base64";
        }
    }

    /// <summary>OTLP configuré (section Observability ou variable standard OpenTelemetry).</summary>
    public static string? OtlpEndpoint(IConfiguration configuration)
        => NonEmpty(configuration["Observability:OtlpEndpoint"]) ?? NonEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    /// <summary>Format des journaux : « json » hors développement par défaut (collecte par la plateforme).</summary>
    public static string LogFormat(IConfiguration configuration, bool isDevelopment)
        => (NonEmpty(configuration["Observability:LogFormat"]) ?? (isDevelopment ? "text" : "json")).ToLowerInvariant();

    public static bool BehindReverseProxy(IConfiguration configuration)
        => bool.TryParse(configuration["ReverseProxy:Enabled"], out var enabled) && enabled;

    /// <summary>Contrôles de configuration, du plus grave au moins grave. <paramref name="production"/> durcit les verdicts.</summary>
    public static IReadOnlyList<ConfigurationCheck> Evaluate(IConfiguration configuration, string environmentName)
    {
        var production = environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase);
        var isDevelopment = environmentName.Equals("Development", StringComparison.OrdinalIgnoreCase);
        var notOk = production ? "fail" : "warn";
        var checks = new List<ConfigurationCheck>();

        var secrets = SecretProblems(configuration);
        checks.Add(new("secrets", "Secrets de production", "security", secrets.Count == 0 ? "pass" : notOk,
            secrets.Count == 0
                ? "clé de signature, clés de chiffrement et connexion fournies par l'environnement"
                : string.Join(" ; ", secrets)));

        // Clé des données personnelles distincte de la clé MFA (audit 2026-10 éd. 2) : à défaut, une seule clé protège
        // les secrets MFA et les identifiants bancaires et fiscaux. Avertissement seulement : une installation existante
        // a chiffré ses données avec la clé MFA, et changer de clé exige de les rechiffrer.
        var dataKey = NonEmpty(configuration["Security:DataEncryptionKey"]);
        var separateDataKey = dataKey is not null && dataKey != NonEmpty(configuration["Security:MfaEncryptionKey"]);
        checks.Add(new("data_key", "Clé des données personnelles", "security", separateDataKey ? "pass" : "warn",
            separateDataKey
                ? "clé distincte de la clé MFA"
                : "Security:DataEncryptionKey absente ou égale à la clé MFA : la même clé protège MFA et données personnelles"));

        var publicUrl = NonEmpty(configuration["PublicUrl"]);
        var proxy = BehindReverseProxy(configuration);
        var urls = (configuration["ASPNETCORE_URLS"] ?? configuration["urls"] ?? "") + " "
                   + string.Join(" ", configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]));
        var https = publicUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true
                    || urls.Contains("https://", StringComparison.OrdinalIgnoreCase);
        checks.Add(new("https", "HTTPS", "security", https ? "pass" : notOk,
            https
                ? (proxy ? $"TLS terminé par le proxy inverse ({publicUrl ?? "adresse publique non renseignée"})" : "point d'écoute HTTPS configuré")
                : "aucune adresse publique HTTPS (PublicUrl) ni point d'écoute HTTPS — les mots de passe et jetons circuleraient en clair"));

        checks.Add(new("reverse_proxy", "Proxy inverse reconnu", "security",
            proxy ? "pass" : "warn",
            proxy
                ? "adresse du client et protocole repris des en-têtes du proxy (limites de débit par client, cookie sécurisé)"
                : "derrière un proxy, activez ReverseProxy:Enabled — sinon toutes les requêtes semblent venir du proxy " +
                  "(une seule limite de connexions pour tout le monde)"));

        var origins = configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
        var insecureOrigins = origins.Where(o => !o.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                                 || o.Contains("localhost", StringComparison.OrdinalIgnoreCase)).ToList();
        checks.Add(new("cors", "Origines autorisées (CORS)", "security",
            origins.Length > 0 && insecureOrigins.Count == 0 ? "pass" : "warn",
            origins.Length == 0 ? "aucune origine déclarée (interface servie par la même adresse que l'API)"
                : insecureOrigins.Count == 0 ? string.Join(", ", origins)
                : "origines de développement ou non chiffrées : " + string.Join(", ", insecureOrigins)));

        var hosts = configuration["AllowedHosts"] ?? "*";
        checks.Add(new("allowed_hosts", "Noms d'hôte acceptés", "security", hosts.Trim() == "*" ? "warn" : "pass",
            hosts.Trim() == "*" ? "tous les noms d'hôte acceptés — renseignez AllowedHosts avec le domaine public" : hosts));

        // Finalisation phase 19 : la limite de connexion est réglable pour la suite E2E — jamais élargie en production.
        var loginLimit = int.TryParse(configuration["Security:LoginRateLimitPerMinute"], out var l) ? l : 10;
        checks.Add(new("login_rate_limit", "Limite de connexion par adresse", "security",
            loginLimit <= 10 ? "pass" : notOk,
            loginLimit <= 10 ? $"{loginLimit} connexions par minute et par adresse"
                : $"limite élargie à {loginLimit} connexions par minute (réglage de test) — revenir à 10 (Security:LoginRateLimitPerMinute)"));

        var otlp = OtlpEndpoint(configuration);
        checks.Add(new("observability", "Traces et métriques (OpenTelemetry)", "monitoring", otlp is null ? "warn" : "pass",
            otlp is null ? "aucun collecteur OTLP configuré (Observability:OtlpEndpoint)" : $"export OTLP vers {otlp}"));

        var logFormat = LogFormat(configuration, isDevelopment);
        checks.Add(new("structured_logs", "Journaux structurés", "monitoring", logFormat == "json" ? "pass" : "warn",
            logFormat == "json" ? "journaux JSON sur la sortie standard (corrélation, trace, utilisateur, code d'erreur)"
                : "journaux texte — passez Observability:LogFormat à « json » pour la collecte"));

        var scheduler = bool.TryParse(configuration["Scheduler:Enabled"], out var s) && s;
        checks.Add(new("scheduler", "Tâches planifiées", "monitoring", scheduler ? "pass" : "warn",
            scheduler ? "planificateur actif (relances, alertes, purges, contrôles)"
                : "planificateur désactivé — relances, alertes et purges ne tournent pas (Scheduler:Enabled)"));

        var smtp = bool.TryParse(configuration["Notifications:Smtp:Enabled"], out var m) && m;
        checks.Add(new("email", "Envoi d'e-mails", "monitoring", smtp ? "pass" : "warn",
            smtp ? "serveur SMTP configuré" : "aucun serveur SMTP — mot de passe oublié et alertes par e-mail ne partent pas"));

        return checks;
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Environnement d'exécution vu par l'infrastructure (qui ne dépend pas de l'hôte web) : nom de l'environnement,
/// heure de démarrage, version et commit de l'image (variables <c>NEXORA_VERSION</c>, <c>NEXORA_COMMIT</c> posées
/// à la construction de l'image).
/// </summary>
public sealed record RuntimeEnvironment(string Name, DateTimeOffset StartedAt, string Version, string? Commit)
{
    public bool IsProduction => Name.Equals("Production", StringComparison.OrdinalIgnoreCase);

    public static RuntimeEnvironment From(IConfiguration configuration, string environmentName)
    {
        var assembly = typeof(RuntimeEnvironment).Assembly;
        var informational = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
        var version = configuration["NEXORA_VERSION"] is { Length: > 0 } v ? v
            : informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        var commit = configuration["NEXORA_COMMIT"] is { Length: > 0 } c ? c
            : informational?.Split('+') is [_, var sha, ..] ? sha : null;
        return new RuntimeEnvironment(environmentName, DateTimeOffset.UtcNow, version, commit);
    }
}
