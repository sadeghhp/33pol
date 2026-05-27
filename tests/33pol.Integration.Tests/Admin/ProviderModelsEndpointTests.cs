using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pol33.Api.Services;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class ProviderModelsEndpointTests
{
    [Fact]
    public async Task GetProviders_WithAdminKey_ReturnsCatalog()
    {
        const string adminKey = "sk-33pol-providers-admin";
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(adminApiKey: adminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);

        var response = await client.GetAsync("/admin/api/providers/catalog");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("openrouter");
        json.Should().Contain("together");
        json.Should().Contain("custom");
    }

    [Fact]
    public async Task PostTogetherModels_WithAdminKey_ReturnsList()
    {
        const string adminKey = "sk-33pol-together-admin";
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            adminApiKey: adminKey,
            configureSettings: settings => settings["TOGETHER_API_KEY"] = "together_token")
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<OpenAiCompatibleProviderModelsClient>();
                    services.AddSingleton(new OpenAiCompatibleProviderModelsClient(new HttpClient(new StubModelsHandler())));
                });
            });

        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/providers/together/models",
            new { envVar = "TOGETHER_API_KEY" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"provider\":\"together\"");
        json.Should().Contain("meta-llama/Llama-3-8b");
    }

    private sealed class StubModelsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body =
                """
                {
                  "data": [
                    { "id": "meta-llama/Llama-3-8b", "name": "Llama 3 8B", "context_length": 8192 }
                  ]
                }
                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
