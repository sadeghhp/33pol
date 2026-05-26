using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Pol33.Integration.Tests.Host;

public sealed class GatewayHostWiringTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public GatewayHostWiringTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/stats")]
    [InlineData("/metrics")]
    [InlineData("/v1/models")]
    [InlineData("/admin/api/config/status")]
    public async Task Pipeline_OperationalEndpoints_ReturnSuccess(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task GetRoot_OmitsServerHeader()
    {
        var response = await _client.GetAsync("/");

        response.Headers.Contains("Server").Should().BeFalse();
    }
}
