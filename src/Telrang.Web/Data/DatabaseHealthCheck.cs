using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Telrang.Web.Data;

/// <summary>Healthy when the database answers. Used by /healthz.</summary>
public sealed class DatabaseHealthCheck(ApplicationDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database unreachable.");
}
