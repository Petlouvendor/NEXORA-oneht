namespace Nexora.ApiTests;

/// <summary>
/// Base visée par les tests d'API (audit 2026-10, T-002). Par défaut la base de développement <c>nexora</c>, comme
/// avant ; <c>NEXORA_API_TEST_DB</c> désigne une autre base (un clone jetable) pour lancer la suite sans écrire dans la
/// base de développement, qui sert aussi de modèle aux tests d'intégration. Les fabriques sans surcharge (sonde de
/// santé, assistant d'installation, garde des secrets) n'écrivent rien et gardent leur propre configuration.
/// </summary>
internal static class ApiTestDatabase
{
    public static string Name => Environment.GetEnvironmentVariable("NEXORA_API_TEST_DB") is { Length: > 0 } db ? db : "nexora";

    /// <summary>Rôle applicatif (sans BYPASSRLS), comme l'API en production.</summary>
    public static string App =>
        $"Host=localhost;Port=5432;Database={Name};Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";

    /// <summary>Administrateur, pour préparer ou vérifier des données hors de l'API.</summary>
    public static string Admin =>
        $"Host=localhost;Port=5432;Database={Name};Username=postgres;Password={Environment.GetEnvironmentVariable("NEXORA_IT_ADMIN_PASSWORD") ?? "Admin@2023"};Search Path=nexora,public";

}
