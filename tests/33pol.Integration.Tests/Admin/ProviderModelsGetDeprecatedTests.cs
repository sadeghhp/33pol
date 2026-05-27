using System.Net;
using System.Net.Http.Headers;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class ProviderModelsGetDeprecatedTests
{
    [Theory]
    [InlineData("/admin/api/providers/together/models")]
    [InlineData("/admin/api/providers/openrouter/models")]
    [InlineData("/admin/api/providers/models?modelsUrl=https://api.example.com/v1/models&envVar=OPENROUTER_API_KEY")]
    public async Task GetProviderModels_Returns405(string path)
    {
        const string adminKey = "sk-33pol-get-deprecated-admin";
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(adminApiKey: adminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
