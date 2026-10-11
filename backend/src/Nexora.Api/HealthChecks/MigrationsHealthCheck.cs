using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nexora.Infrastructure.Setup;

namespace Nexora.Api.HealthChecks;

/// <summary>
/// Readiness (finalisation phase 18) : une instance dont la base n'a pas reçu les migrations de sa version n'est pas
/// prête — elle échouerait sur des colonnes absentes. Une base modifiée ou plus récente que l'application reste
/// servie mais signalée (dégradé).
/// </summary>
public sealed class MigrationsHealthCheck(MigrationStatusProbe probe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var status = await probe.GetAsync(ct);
            if (!status.UpToDate)
                return HealthCheckResult.Unhealthy($"{status.Pending.Count} migration(s) en attente (dernière livrée : {status.LatestEmbedded})");
            if (status.Drifted.Count > 0)
                return HealthCheckResult.Degraded($"{status.Drifted.Count} migration(s) appliquée(s) modifiée(s) depuis");
            if (status.Unknown.Count > 0)
                return HealthCheckResult.Degraded($"base plus récente que l'application ({string.Join(", ", status.Unknown)})");
            return HealthCheckResult.Healthy($"schéma à jour ({status.LatestApplied})");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("état des migrations illisible", ex);
        }
    }
}
