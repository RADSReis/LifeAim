using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LifeAim.IntegrationTests;

/// <summary>
/// The API as the tests run it, against the database in ConnectionStrings:LifeAim (local User Secrets, or
/// the CI's throwaway PostgreSQL), already migrated by LifeAim.Migrator.
/// </summary>
public sealed class LifeAimApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Tests run as Development, which loads the developer's User Secrets: blank the token so a test
        // run never connects the real bot (it would answer DMs alongside the running API).
        builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Discord:Token"] = "" }));

        // No background jobs either (see WithoutBackgroundJobs).
        builder.ConfigureTestServices(WithoutBackgroundJobs);
    }

    /// <summary>
    /// Removes Quartz's hosted service: the plan proposals job would pick up the tests' rows (and call the real AI)
    /// in the middle of a test. The tests that need the job run it themselves.
    /// </summary>
    public static void WithoutBackgroundJobs(IServiceCollection services)
    {
        foreach (var quartz in services
                     .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "QuartzHostedService")
                     .ToList())
        {
            services.Remove(quartz);
        }
    }
}
