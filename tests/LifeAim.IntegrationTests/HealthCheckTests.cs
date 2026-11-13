using System.Net;

namespace LifeAim.IntegrationTests;

public class HealthCheckTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>
{
    [Fact]
    public async Task Health_endpoint_returns_healthy()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
