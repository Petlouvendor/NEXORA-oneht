namespace Nexora.Infrastructure.Persistence;

/// <summary>Chaînes de connexion : runtime à privilèges minimaux, séparé de la migration (Phase 3 §14).</summary>
public sealed class NexoraDbOptions
{
    public const string SectionName = "Database";

    /// <summary>Connexion runtime (rôle nexora_app, sans BYPASSRLS).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public int MaxRetryCount { get; set; } = 3;
    public int CommandTimeoutSeconds { get; set; } = 30;
}
