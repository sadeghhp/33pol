using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pol33.Api.Services;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class OpenRouterProviderModelsEndpointTests
{
    [Fact]
    public async Task GetOpenRouterModels_WithAdminKey_ReturnsList()
    {
        const string adminKey = "sk-33pol-openrouter-admin";

        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            adminApiKey: adminKey,
            configureSettings: settings =>
            {
                settings["OPENROUTER_API_KEY"] = "or_token";
            })
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<OpenAiCompatibleProviderModelsClient>();
                    services.AddSingleton(new OpenAiCompatibleProviderModelsClient(new HttpClient(new OpenRouterModelsStubHandler())));
                });
            });

        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);

        var response = await client.GetAsync("/admin/api/providers/openrouter/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"data\"");
        json.Should().Contain("anthropic/claude-3.5-sonnet");
    }

    [Fact]
    public async Task GetOpenRouterModels_WithoutKey_Returns401()
    {
        const string adminKey = "sk-33pol-openrouter-admin-2";
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(adminApiKey: adminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/admin/api/providers/openrouter/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed class OpenRouterModelsStubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body =
                """
                {
                  "data": [
                    { "id": "anthropic/claude-3.5-sonnet", "name": "Claude 3.5 Sonnet", "context_length": 200000 },
                    { "id": "openai/gpt-4o-mini", "name": "GPT-4o mini", "context_length": 128000 }
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

