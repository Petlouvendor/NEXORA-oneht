using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Api.HealthChecks;

public sealed class DatabaseHealthCheck(NexoraDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var canConnect = await db.Database.CanConnectAsync(ct);
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL joignable")
                : HealthCheckResult.Unhealthy("PostgreSQL injoignable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Échec de connexion PostgreSQL", ex);
        }
    }
}
