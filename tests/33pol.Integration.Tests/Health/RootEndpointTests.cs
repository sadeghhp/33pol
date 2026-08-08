using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Pol33.Integration.Tests.Health;

public sealed class RootEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public RootEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetRoot_ReturnsServiceMetadata()
    {
        var response = await _client.GetAsync("/");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RootResponse>();

        body.Should().NotBeNull();
        body!.Name.Should().Be("33pol");
        body.Version.Should().NotBeNullOrWhiteSpace();
        body.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$");
        body.Documentation.Should().NotBeNull();
        body.Documentation!.Readme.Should().Be("README.md");
        body.Documentation.Architecture.Should().Be("docs/architecture.md");
    }

    private sealed record RootResponse(
        string Name,
        string Version,
        DocumentationLinks? Documentation);

    private sealed record DocumentationLinks(
        string Readme,
        string Architecture);
}
