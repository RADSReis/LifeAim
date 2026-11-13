using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace LifeAim.BuildingBlocks.Database;

/// <summary>Healthy when PostgreSQL answers the cheapest possible query.</summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // The data source borrows a connection from the pool and gives it back afterwards.
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "PostgreSQL is unreachable.", ex);
        }
    }
}
