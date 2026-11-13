using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
    }
}
