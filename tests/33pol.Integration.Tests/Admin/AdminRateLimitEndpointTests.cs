using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class AdminRateLimitEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    [Fact]
    public async Task GetRateLimits_WithoutAuth_Returns401()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/api/rate-limits");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetRateLimits_WithAdmin_ReturnsDefaultAndPlans()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            configureSettings: settings =>
            {
                settings["RateLimiting:Default:Rpm"] = "77";
                settings["RateLimiting:Plans:standard:Rpm"] = "150";
            });
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.GetAsync("/admin/api/rate-limits");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("default").GetProperty("rpm").GetInt32().Should().Be(77);
        json.RootElement.GetProperty("plans").GetProperty("standard").GetProperty("rpm").GetInt32().Should().Be(150);
    }

    [Fact]
    public async Task PutRateLimits_InvalidRpm_Returns400WithMessage()
    {
        await using var factory = CreateFactoryWithWritableAppSettings();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                @default = new { rpm = 0, burst = 0, maxConcurrentStreams = 0 },
                plans = new Dictionary<string, object>(),
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("message").GetString().Should().Contain("rpm");
    }

    [Fact]
    public async Task PutRateLimits_ValidPayload_UpdatesGetResponse()
    {
        await using var factory = CreateFactoryWithWritableAppSettings();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var putResponse = await client.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                @default = new { rpm = 55, burst = 5, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>
                {
                    ["pro"] = new { rpm = 200, burst = 20, maxConcurrentStreams = 15 },
                },
            });
        putResponse.EnsureSuccessStatusCode();

        var getResponse = await client.GetAsync("/admin/api/rate-limits");
        getResponse.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("default").GetProperty("rpm").GetInt32().Should().Be(55);
        json.RootElement.GetProperty("plans").GetProperty("pro").GetProperty("rpm").GetInt32().Should().Be(200);
    }

    [Fact]
    public async Task PutRateLimits_ReducesDefaultRpm_EnforcedOnInference()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = CreateFactoryWithWritableAppSettings(handler);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var putResponse = await admin.PutAsJsonAsync(
            "/admin/api/rate-limits",
            new
            {
                @default = new { rpm = 1, burst = 0, maxConcurrentStreams = 5 },
                plans = new Dictionary<string, object>(),
            });
        putResponse.EnsureSuccessStatusCode();

        var createKey = await admin.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createKey.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createKey.Content.ReadAsStringAsync());
        var keyId = created.RootElement.GetProperty("id").GetGuid();
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        var grantResponse = await admin.PutAsJsonAsync(
            $"/admin/api/keys/{keyId}/model-grants",
            new { modelIds = new[] { "local-mock" } });
        grantResponse.EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        var body = JsonSerializer.Serialize(new
        {
            model = "local-mock",
            messages = new[] { new { role = "user", content = "hi" } },
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var first = await client.PostAsync("/v1/chat/completions", content);
        first.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadGateway);

        using var content2 = new StringContent(body, Encoding.UTF8, "application/json");
        var second = await client.PostAsync("/v1/chat/completions", content2);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private static WebApplicationFactory<Program> CreateFactoryWithWritableAppSettings(
        HttpMessageHandler? upstreamHandler = null)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "33pol-rate-limit-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(
            Path.Combine(contentRoot, "appsettings.json"),
            """
            {
              "RateLimiting": {
                "Default": { "Rpm": 10, "Burst": 1, "MaxConcurrentStreams": 1 },
                "Plans": {}
              },
              "Gateway": {
                "Bootstrap": { "Enabled": false },
                "ModelsConfigPath": "config/models.json"
              }
            }
            """);

        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            upstreamHandler: upstreamHandler,
            configureSettings: settings => settings["Gateway:AppSettingsPath"] = "appsettings.json")
            .WithWebHostBuilder(builder => builder.UseContentRoot(contentRoot));
    }

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}
