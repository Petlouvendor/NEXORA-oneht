using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Integrations;
using Nexora.Application.Observability;
using Nexora.Application.Operations;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Scheduling;

/// <summary>Options du planificateur de fond (Phase 16 §22). Désactivé par défaut.</summary>
public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 60;
}

/// <summary>Résumé observable d'un passage du planificateur.</summary>
public sealed record SchedulerTickResult(
    int Companies, int AutomationsRun, int WebhookDeliveries, int PaymentIntentsExpired, int Failures,
    int LotsExpired = 0, int NotificationsDispatched = 0);

/// <summary>
/// Cœur du planificateur (Phase 16 §22) : pour chaque entreprise active, en portée système,
/// exécute les traitements asynchrones <b>idempotents</b> (relances/alertes dues, livraisons de
/// webhooks sortants dues, expiration des intentions de paiement échues, etc.). Isolé du service
/// de fond pour être testable.
///
/// Depuis la Phase 41 (§7-9, §51), chaque traitement est un <b>job nommé</b> : le
/// <see cref="Application.Operations.IJobRegistry"/> vérifie qu'il est activé avant de le lancer et
/// journalise chaque passage (durée, résultat, erreurs) — pause/reprise/exécution manuelle
/// deviennent possibles sans redéployer.
/// </summary>
public sealed class SchedulerRunner(
    NexoraDbContext db,
    ITenantContext tenant,
    IAutomationService automations,
    IOutboundWebhookService outbound,
    IPaymentGateway payments,
    Nexora.Application.Inventory.ILotService lots,
    Nexora.Application.Notifications.INotificationDispatcher notifications,
    Nexora.Application.Continuity.IContinuityService continuity,
    Nexora.Application.Platform.ISubscriptionService subscriptions,
    Nexora.Application.Platform.IPlatformBillingService platformBilling,
    Nexora.Application.Migration.IImportService imports,
    IRetentionPurgeService purge,
    Nexora.Application.Reporting.IReportScheduleService reportSchedules,
    Nexora.Application.Reporting.IInsightService insights,
    Nexora.Application.Intelligence.IRecommendationService recommendations,
    Nexora.Application.Intelligence.IScoringService scoring,
    Nexora.Application.Ged.IDocumentRetentionService gedRetention,
    Nexora.Application.Ged.ISignatureService signatures,
    Nexora.Application.Security.ISecurityCertificateService certificates,
    Nexora.Application.Customers.ICreditAlertScanner creditAlerts,
    Nexora.Application.Credit.CollectionsService collections,
    Nexora.Application.Payroll.IPayrollAlerts payrollAlerts,
    Nexora.Application.Finance.Periods.IAccountingPeriodService accountingPeriods,
    IJobRegistry jobs,
    ILogger<SchedulerRunner> logger)
{
    public async Task<SchedulerTickResult> RunOnceAsync(CancellationToken ct = default)
    {
        var companies = await LoadActiveCompaniesAsync(ct);
        var jobActions = BuildJobActions(companies);
        int failures = 0;

        async Task<int> Run(string key)
        {
            var (count, f) = await RunJobAsync(key, jobActions[key], "scheduler", null, ct);
            failures += f;
            return count;
        }

        var automationsRun = await Run("automations.due");
        var deliveries = await Run("webhooks.deliver");
        var expired = await Run("payments.expire_stale");
        var lotsExpired = await Run("lots.sweep_expired");
        await Run("continuity.health_check");
        var dispatched = await Run("notifications.dispatch");
        await Run("subscriptions.lifecycle");
        await Run("imports.resume_stalled");
        await Run("maintenance.purge");
        await Run("reports.deliver_scheduled");
        await Run("insights.scan");
        await Run("intelligence.generate_recommendations");
        await Run("ged.apply_retention");
        await Run("ged.expire_signatures");
        await Run("security.check_certificates");
        await Run("credit.settle_promises");
        await Run("credit.scan_alerts");
        await Run("payroll.scan_alerts");
        await Run("accounting.open_next_year");

        var result = new SchedulerTickResult(companies.Count, automationsRun, deliveries, expired, failures, lotsExpired, dispatched);
        if (automationsRun + deliveries + expired + failures + lotsExpired + dispatched > 0)
            logger.LogInformation(
                "Planificateur : {Companies} entreprise(s), {Automations} automatisation(s), {Deliveries} livraison(s), {Expired} intention(s) expirée(s), {Lots} lot(s) expiré(s), {Failures} échec(s)",
                result.Companies, result.AutomationsRun, result.WebhookDeliveries, result.PaymentIntentsExpired, result.LotsExpired, result.Failures);
        return result;
    }

    /// <summary>
    /// Exécute un seul job immédiatement, hors cadence normale (§7, §51 « Exécuter maintenant »).
    /// Contrairement au passage normal, une pause n'empêche pas un déclenchement manuel explicite.
    /// </summary>
    public async Task<int> RunSingleJobAsync(string key, Guid? triggeredByUser, CancellationToken ct = default)
    {
        var companies = await LoadActiveCompaniesAsync(ct);
        var jobActions = BuildJobActions(companies);
        if (!jobActions.TryGetValue(key, out var action))
            throw new InvalidOperationException($"Job inconnu : {key}");
        var (count, _) = await RunJobAsync(key, action, "manual", triggeredByUser, ct, force: true);
        return count;
    }

    // ------------------------------------------------------- Construction des jobs

    private Dictionary<string, Func<CancellationToken, Task<(int Count, int Failures)>>> BuildJobActions(List<CompanyRow> companies) => new()
    {
        ["automations.due"] = token => PerCompanyAsync(companies, automations.RunDueAsync, "automatisations", token),
        ["webhooks.deliver"] = token => PerCompanyAsync(companies, outbound.DeliverDueAsync, "webhooks", token),
        ["payments.expire_stale"] = token => PerCompanyAsync(companies, payments.ExpireStaleAsync, "paiements", token),
        ["lots.sweep_expired"] = token => PerCompanyAsync(companies, async t =>
        {
            var sweep = await lots.SweepExpiredAsync(t);
            return sweep.IsSuccess ? sweep.Value!.LotsExpired : 0;
        }, "lots", token),
        ["continuity.health_check"] = token => PerCompanyAsync(companies, async t =>
        {
            await continuity.RunHealthChecksAsync(t);
            return 0;
        }, "continuité", token),
        ["notifications.dispatch"] = token => GlobalAsync(() => notifications.DispatchDueAsync(token), "notifications"),
        ["subscriptions.lifecycle"] = token => GlobalAsync(async () =>
        {
            var transitions = await subscriptions.RunDueAsync(token);
            await platformBilling.ReconcileAsync(token);
            return transitions;
        }, "abonnements"),
        ["imports.resume_stalled"] = token => GlobalAsync(async () => { await imports.ResumeStalledAsync(token); return 0; }, "imports"),
        ["maintenance.purge"] = token => GlobalAsync(() => purge.RunDueAsync(token), "purge technique"),
        ["reports.deliver_scheduled"] = token => PerCompanyAsync(companies, reportSchedules.RunDueAsync, "rapports planifiés", token),
        ["insights.scan"] = token => PerCompanyAsync(companies, insights.ScanAsync, "insights", token),
        ["intelligence.generate_recommendations"] = token => PerCompanyAsync(companies, async t =>
        {
            var created = await recommendations.GenerateAsync(t);
            await scoring.ComputeAsync(t);
            return created;
        }, "recommandations", token),
        ["ged.apply_retention"] = token => PerCompanyAsync(companies, gedRetention.ApplyRetentionAsync, "rétention documentaire", token),
        ["ged.expire_signatures"] = token => PerCompanyAsync(companies, signatures.ExpireDueAsync, "expiration signatures", token),
        ["security.check_certificates"] = token => GlobalAsync(() => certificates.RefreshExpiryStatusAsync(token), "certificats"),
        ["credit.settle_promises"] = token => PerCompanyAsync(companies, collections.SettleDuePromisesAsync, "échéance des promesses", token),
        ["credit.scan_alerts"] = token => PerCompanyAsync(companies, creditAlerts.ScanAsync, "alertes crédit", token),
        ["payroll.scan_alerts"] = token => PerCompanyAsync(companies, payrollAlerts.ScanAsync, "alertes paie", token),
        ["accounting.open_next_year"] = token => PerCompanyAsync(companies, accountingPeriods.OpenNextYearIfDueAsync, "ouverture de l'exercice suivant", token),
    };

    private async Task<List<CompanyRow>> LoadActiveCompaniesAsync(CancellationToken ct)
    {
        using var _ = tenant.BeginSystemScope();
        return await db.Database.SqlQuery<CompanyRow>($"""
            SELECT id AS "Id", group_id AS "GroupId" FROM nexora.company WHERE status = 'active'
            """).ToListAsync(ct);
    }

    private async Task<(int Count, int Failures)> PerCompanyAsync(
        List<CompanyRow> companies, Func<CancellationToken, Task<int>> action, string label, CancellationToken ct)
    {
        int count = 0, failures = 0;
        foreach (var c in companies)
        {
            try
            {
                tenant.SetScope([c.Id], c.GroupId is { } g ? [g] : [], c.Id);
                count += await action(ct);
            }
            catch (Exception ex)
            {
                failures++;
                logger.LogWarning(ex, "Planificateur : échec {Label} pour l'entreprise {CompanyId}", label, c.Id);
            }
        }
        return (count, failures);
    }

    private async Task<(int Count, int Failures)> GlobalAsync(Func<Task<int>> action, string label)
    {
        try { return (await action(), 0); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Planificateur : échec {Label}", label);
            return (0, 1);
        }
    }

    private async Task<(int Count, int Failures)> RunJobAsync(
        string key, Func<CancellationToken, Task<(int Count, int Failures)>> action,
        string triggeredBy, Guid? triggeredByUser, CancellationToken ct, bool force = false)
    {
        if (!force && !await jobs.IsEnabledAsync(key, ct)) return (0, 0);
        var runId = await jobs.StartRunAsync(key, triggeredBy, triggeredByUser, ct);
        using var activity = NexoraMetrics.ActivitySource.StartActivity($"job {key}");
        activity?.SetTag("nexora.job", key);
        activity?.SetTag("nexora.triggered_by", triggeredBy);
        try
        {
            var (count, failures) = await action(ct);
            await jobs.FinishRunAsync(runId, failures == 0, count, failures > 0 ? $"{failures} échec(s) entreprise" : null, ct);
            NexoraMetrics.RecordJob(key, failures == 0 ? "succeeded" : "partial");
            return (count, failures);
        }
        catch (Exception ex)
        {
            NexoraMetrics.RecordJob(key, "failed");
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            await jobs.FinishRunAsync(runId, false, 0, ex.Message, ct);
            logger.LogWarning(ex, "Planificateur : échec du job {Key}", key);
            return (0, 1);
        }
    }

    private sealed record CompanyRow(Guid Id, Guid? GroupId);
}
