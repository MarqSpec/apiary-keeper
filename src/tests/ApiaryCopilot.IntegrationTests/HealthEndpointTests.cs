using System.Net;
using System.Text.Json;

namespace ApiaryCopilot.IntegrationTests;

[Trait("Deployment", "None")]
[Collection(SharedServer.Name)]
public sealed class HealthEndpointTests(AppServerFixture server)
{
    [Fact]
    public async Task Health_ShouldReturn200WithRunningVersion_WhenHostIsReady()
    {
        using var response = await server.Client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("version").GetString().Should().Be(server.ExpectedVersion);
    }

    [Fact]
    public async Task WeatherForecast_ShouldReturn404_WhenTemplateEndpointIsRemoved()
    {
        using var response = await server.Client.GetAsync("/weatherforecast");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
