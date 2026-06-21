using frcastr.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace frcastr.Web.Health;

public class DbHealthCheck(ApplicationDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            if (await dbContext.Database.CanConnectAsync(ct))
                return HealthCheckResult.Healthy();
            return HealthCheckResult.Unhealthy("Cannot connect to database.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
