namespace Nexora.Api.Security;

/// <summary>
/// Résumé fonctionnel §18, §21 (« ne jamais stocker les secrets dans le code ») : hors environnement
/// de développement, l'API refuse de démarrer si un secret manque ou retombe sur une valeur de repli
/// connue — sinon un oubli de configuration permettrait de forger des jetons ou de déchiffrer les
/// secrets MFA avec une clé publique (celle écrite dans le code). Aucun secret n'est affiché.
/// </summary>
public static class StartupSecretsGuard
{
    private static readonly string[] WeakMarkers = ["dev-only", "insecure", "change-me", "changeme"];

    public static IReadOnlyList<string> Problems(IConfiguration configuration)
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
        else
        {
            try
            {
                if (Convert.FromBase64String(mfaKey).Length != 32)
                    problems.Add("Security:MfaEncryptionKey doit décoder en 32 octets (base64)");
            }
            catch (FormatException)
            {
                problems.Add("Security:MfaEncryptionKey n'est pas du base64");
            }
        }

        if (string.IsNullOrWhiteSpace(configuration["Database:ConnectionString"]))
            problems.Add("Database:ConnectionString absente");

        return problems;
    }

    /// <summary>Lève si un secret de production manque (sauf en Development).</summary>
    public static void EnsureConfigured(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;
        var problems = Problems(configuration);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"Démarrage refusé ({environment.EnvironmentName}) — secrets de production manquants ou faibles : " +
                string.Join(" ; ", problems) + ". Fournissez-les par variables d'environnement ou gestionnaire de secrets.");
    }
}
