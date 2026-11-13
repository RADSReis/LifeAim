using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LifeAim.BuildingBlocks.Database;

public static class DatabaseServiceCollectionExtensions
{
    public const string ConnectionStringName = "LifeAim";

    /// <summary>
    /// Registers the one NpgsqlDataSource (connection pool) every module uses. Each module still writes
    /// its own SQL, only against its own schema. LifeAim does not run without a database, so a missing
    /// connection string stops the API at startup instead of failing later.
    /// </summary>
    public static IServiceCollection AddLifeAimDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is not configured.");
        }

        services.AddNpgsqlDataSource(connectionString);
        return services;
    }

    /// <summary>Adds the PostgreSQL check to /health.</summary>
    public static IHealthChecksBuilder AddLifeAimDatabase(this IHealthChecksBuilder builder)
    {
        return builder.AddCheck<PostgresHealthCheck>("postgres");
    }
}
