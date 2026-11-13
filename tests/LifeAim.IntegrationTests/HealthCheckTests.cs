using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LifeAim.IntegrationTests;

public class HealthCheckTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // Tests run as Development, which loads the developer's User Secrets: blank the token so a test
    // run never connects the real bot (it would answer DMs alongside the running API).
    private readonly WebApplicationFactory<Program> _factory = factory.WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Discord:Token"] = "" })));

    [Fact]
    public async Task Health_endpoint_returns_healthy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
