using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Nexora.Application.Observability;

/// <summary>
/// Métriques métier et de sécurité (finalisation phase 18, §39 « métriques, monitoring »). Un seul <see cref="Meter"/>
/// « Nexora », exporté par OpenTelemetry quand un collecteur est configuré ; sans collecteur, chaque appel ne coûte
/// qu'un test « aucun auditeur ». Les étiquettes restent à faible cardinalité : jamais d'identifiant d'utilisateur,
/// d'entreprise ou de montant.
/// </summary>
public static class NexoraMetrics
{
    public const string MeterName = "Nexora";
    public const string ActivitySourceName = "Nexora";

    private static readonly Meter Meter = new(MeterName, "1.0");

    /// <summary>Traces des traitements internes (planificateur, migrations) en plus des requêtes HTTP.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0");

    private static readonly Counter<long> Errors = Meter.CreateCounter<long>(
        "nexora.errors", description: "Réponses d'erreur par code métier et statut HTTP.");
    private static readonly Counter<long> Logins = Meter.CreateCounter<long>(
        "nexora.auth.logins", description: "Tentatives de connexion par issue.");
    private static readonly Counter<long> Refreshes = Meter.CreateCounter<long>(
        "nexora.auth.refreshes", description: "Renouvellements de session par issue (rotation, vol détecté…).");
    private static readonly Counter<long> StepUps = Meter.CreateCounter<long>(
        "nexora.security.step_ups", description: "Confirmations fortes par catégorie et issue.");
    private static readonly Counter<long> Checkouts = Meter.CreateCounter<long>(
        "nexora.sales.checkouts", description: "Ventes encaissées (caisse et commandes).");
    private static readonly Counter<long> Jobs = Meter.CreateCounter<long>(
        "nexora.scheduler.jobs", description: "Exécutions de tâches planifiées par tâche et issue.");

    public static void RecordError(string code, int status)
        => Errors.Add(1, new("code", code), new("status", status));

    public static void RecordLogin(string outcome) => Logins.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordRefresh(string outcome) => Refreshes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordStepUp(string purpose, string outcome)
        => StepUps.Add(1, new("purpose", purpose), new("outcome", outcome));

    public static void RecordCheckout(string channel) => Checkouts.Add(1, new KeyValuePair<string, object?>("channel", channel));

    // « task » et non « job » : « job » est réservé par Prometheus (nom du service).
    public static void RecordJob(string task, string outcome) => Jobs.Add(1, new("task", task), new("outcome", outcome));
}
