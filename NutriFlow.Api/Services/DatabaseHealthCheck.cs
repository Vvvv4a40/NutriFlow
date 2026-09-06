using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Services;

public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DatabaseHealthCheck(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            NutriFlowDbContext dbContext =
                scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();

            if (!await dbContext.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy(
                    "The SQLite database cannot be reached.");
            }

            string[] pendingMigrations = (await dbContext.Database
                    .GetPendingMigrationsAsync(cancellationToken))
                .ToArray();

            return pendingMigrations.Length == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    "The SQLite schema has pending migrations.",
                    data: new Dictionary<string, object>
                    {
                        ["pendingMigrations"] = pendingMigrations
                    });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "The SQLite readiness check failed.",
                exception);
        }
    }
}
