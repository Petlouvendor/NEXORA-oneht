namespace Nexora.ApiTests;

/// <summary>
/// Base visée par les tests d'API (audit 2026-10, T-002). Par défaut <c>nexora_api_it</c>, copie de la base de
/// développement (<c>CREATE DATABASE nexora_api_it TEMPLATE nexora</c>) : la suite n'écrit plus dans <c>nexora</c>, qui
/// sert de modèle aux tests d'intégration (audit 2026-10 éd. 2). <c>NEXORA_API_TEST_DB</c> désigne une autre base. Les
/// fabriques sans surcharge (sonde de santé, assistant d'installation, garde des secrets) n'écrivent rien et gardent
/// leur propre configuration.
/// </summary>
internal static class ApiTestDatabase
{
    public static string Name => Environment.GetEnvironmentVariable("NEXORA_API_TEST_DB") is { Length: > 0 } db ? db : "nexora_api_it";

    /// <summary>Rôle applicatif (sans BYPASSRLS), comme l'API en production.</summary>
    public static string App =>
        $"Host=localhost;Port=5432;Database={Name};Username=nexora_app;Password=nexora_app_dev;Search Path=nexora,public;Include Error Detail=true";

    /// <summary>Administrateur, pour préparer ou vérifier des données hors de l'API.</summary>
    public static string Admin =>
        $"Host=localhost;Port=5432;Database={Name};Username=postgres;Password={Environment.GetEnvironmentVariable("NEXORA_IT_ADMIN_PASSWORD") ?? "Admin@2023"};Search Path=nexora,public";

}
