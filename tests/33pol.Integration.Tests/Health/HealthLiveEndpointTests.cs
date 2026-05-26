using Microsoft.AspNetCore.Mvc.Testing;

namespace Pol33.Integration.Tests.Health;

public sealed class HealthLiveEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthLiveEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetHealthLive_ReturnsOk()
    {
        var response = await _client.GetAsync("/health/live");

        response.EnsureSuccessStatusCode();
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
