using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pol33.Api.Services;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class ProviderModelsPostEndpointTests
{
    [Fact]
    public async Task PostTogetherModels_WithValidBody_ReturnsList()
    {
        const string adminKey = "sk-33pol-post-together-admin";
        using var factory = CreateFactory(adminKey, settings => settings["TOGETHER_API_KEY"] = "together_token");
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/providers/together/models",
            new { envVar = "TOGETHER_API_KEY" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"provider\":\"together\"");
        json.Should().Contain("meta-llama/Llama-3-8b");
    }

    [Fact]
    public async Task PostTogetherModels_WithSecretEnvVar_Returns400()
    {
        const string adminKey = "sk-33pol-post-secret-admin";
        using var factory = CreateFactory(adminKey);
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/providers/together/models",
            new { envVar = "sk-together-secret-token-0123456789abcdef" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("not the API key");
    }

    [Fact]
    public async Task PostLmStudioModels_DiscoveryNotSupported_Returns400WithGuidance()
    {
        const string adminKey = "sk-33pol-post-lmstudio-admin";
        using var factory = CreateFactory(adminKey);
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/providers/lmstudio/models",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("discovery is not available");
        json.Should().NotContain("blocked");
    }

    [Fact]
    public async Task GetProviders_ExposesSupportsDiscoveryFlag()
    {
        const string adminKey = "sk-33pol-get-providers-flag-admin";
        using var factory = CreateFactory(adminKey);
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var response = await client.GetAsync("/admin/api/providers/catalog");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"id\":\"lmstudio\"");
        json.Should().Contain("\"supportsDiscovery\":false");
        json.Should().Contain("\"supportsDiscovery\":true");
    }

    [Fact]
    public async Task PostCustomModels_WithoutEnvVar_ReturnsList()
    {
        const string adminKey = "sk-33pol-post-custom-noauth-admin";
        using var factory = CreateFactory(adminKey);
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/providers/models",
            new { modelsUrl = "https://api.example.com/v1/models" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"provider\":\"custom\"");
    }

    [Fact]
    public async Task PostCustomModels_Upstream401_Returns502()
    {
        const string adminKey = "sk-33pol-post-custom-401-admin";
        using var factory = CreateFactory(
            adminKey,
            settings => settings["CUSTOM_PROVIDER_API_KEY"] = "custom_token",
            services => services.AddSingleton(
                new OpenAiCompatibleProviderModelsClient(
                    new HttpClient(new UnauthorizedModelsHandler()))));
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/providers/models",
            new
            {
                modelsUrl = "https://api.example.com/v1/models",
                envVar = "CUSTOM_PROVIDER_API_KEY",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("401");
    }

    [Fact]
    public async Task PostCustomModels_WithValidBody_ReturnsList()
    {
        const string adminKey = "sk-33pol-post-custom-admin";
        using var factory = CreateFactory(
            adminKey,
            settings => settings["CUSTOM_PROVIDER_API_KEY"] = "custom_token");
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/providers/models",
            new
            {
                modelsUrl = "https://api.example.com/v1/models",
                envVar = "CUSTOM_PROVIDER_API_KEY"
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"provider\":\"custom\"");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string adminApiKey,
        Action<IDictionary<string, string?>>? configureSettings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
                adminApiKey: adminApiKey,
                configureSettings: configureSettings)
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<OpenAiCompatibleProviderModelsClient>();
                    configureServices?.Invoke(services);
                    if (!services.Any(d => d.ServiceType == typeof(OpenAiCompatibleProviderModelsClient)))
                    {
                        services.AddSingleton(
                            new OpenAiCompatibleProviderModelsClient(new HttpClient(new StubModelsHandler())));
                    }
                });
            });
    }

    private static async Task<HttpClient> CreateAdminClientAsync(WebApplicationFactory<Program> factory, string adminKey)
    {
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
        return client;
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

    private sealed class UnauthorizedModelsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
