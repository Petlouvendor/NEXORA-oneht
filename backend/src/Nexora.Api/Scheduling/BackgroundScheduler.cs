using System.Diagnostics;
using Microsoft.Extensions.Options;
using Nexora.Infrastructure.Scheduling;
using Nexora.Infrastructure.Setup;

namespace Nexora.Api.Scheduling;

/// <summary>
/// Service de fond du monolithe modulaire (Phase 16 §22, §46). À intervalle régulier, délègue à
/// <see cref="SchedulerRunner"/> l'exécution des traitements asynchrones idempotents. Désactivé
/// par défaut (<c>Scheduler:Enabled</c>) : l'hébergement local ou cloud l'active selon le déploiement.
///
/// <para>
/// En mode multi-instance, <see cref="PostgresAdvisoryLock"/> garantit qu'un seul pod exécute
/// le runner à la fois (Production Readiness — REQ-01). Si le lock est déjà détenu par une
/// autre instance, ce passage est ignoré silencieusement — pas d'erreur, pas d'alarme.
/// </para>
/// </summary>
public sealed class BackgroundScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<SchedulerOptions> options,
    SetupState setup,
    PostgresAdvisoryLock advisoryLock,   // Singleton — injecté directement (REQ-01)
    ILogger<BackgroundScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opt = options.Value;

        if (setup.Required)
        {
            logger.LogInformation("Planificateur de fond inactif : application en cours d'installation (§20).");
            return;
        }

        if (!opt.Enabled)
        {
            logger.LogInformation("Planificateur de fond désactivé (Scheduler:Enabled = false).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(opt.IntervalSeconds, 10, 3600));
        logger.LogInformation("Planificateur de fond actif — intervalle {Interval}s.", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var runner = scope.ServiceProvider.GetRequiredService<SchedulerRunner>();

                var sw = Stopwatch.StartNew();

                // TryExecuteAsync : retourne false si une autre instance tient le lock.
                // Le Stopwatch sera utilisé par la tâche 25 (métriques OTel).
                var executed = await advisoryLock.TryExecuteAsync(
                    async ct =>
                    {
                        await runner.RunOnceAsync(ct);
                        logger.LogDebug(
                            "Planificateur : passage exécuté en {ElapsedMs}ms.", sw.ElapsedMilliseconds);
                    },
                    stoppingToken);

                if (!executed)
                {
                    // Comportement normal en déploiement multi-pod : aucune alarme.
                    logger.LogDebug(
                        "Planificateur : lock non disponible (autre instance en cours) — passage ignoré.");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Planificateur de fond : passage en échec.");
            }
        }
    }
}
