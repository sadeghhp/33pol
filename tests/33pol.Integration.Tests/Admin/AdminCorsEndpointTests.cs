using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class AdminCorsEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";
    private const string AllowedOrigin = "https://sadeghhp.github.io";

    [Fact]
    public async Task GetCors_WithoutAuth_Returns401()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/api/cors");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCors_WithAdmin_ReturnsConfiguredOrigins()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            configureSettings: settings =>
            {
                settings["Gateway:Cors:AllowedOrigins:0"] = AllowedOrigin;
            });
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.GetAsync("/admin/api/cors");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var origins = json.RootElement.GetProperty("allowedOrigins");
        origins.GetArrayLength().Should().Be(1);
        origins[0].GetString().Should().Be(AllowedOrigin);
    }

    [Fact]
    public async Task PutCors_InvalidOrigin_Returns400WithMessage()
    {
        await using var factory = CreateFactoryWithWritableAppSettings();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.PutAsJsonAsync(
            "/admin/api/cors",
            new { allowedOrigins = new[] { "https://app.example.com/path" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("message").GetString().Should().Contain("path");
    }

    [Fact]
    public async Task PutCors_ValidPayload_UpdatesGetAndPreflightWithoutRestart()
    {
        await using var factory = CreateFactoryWithWritableAppSettings();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var putResponse = await client.PutAsJsonAsync(
            "/admin/api/cors",
            new { allowedOrigins = new[] { AllowedOrigin, "http://localhost:5173" } });
        putResponse.EnsureSuccessStatusCode();

        var getResponse = await client.GetAsync("/admin/api/cors");
        getResponse.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        var origins = json.RootElement.GetProperty("allowedOrigins")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        origins.Should().Contain(AllowedOrigin);
        origins.Should().Contain("http://localhost:5173");

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/models");
        preflight.Headers.TryAddWithoutValidation("Origin", AllowedOrigin);
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        preflight.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization,content-type");

        var optionsResponse = await factory.CreateClient().SendAsync(preflight);
        optionsResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        optionsResponse.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be(AllowedOrigin);
        optionsResponse.Headers.Contains("Access-Control-Max-Age").Should().BeTrue();
    }

    private static WebApplicationFactory<Program> CreateFactoryWithWritableAppSettings()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "33pol-cors-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(
            Path.Combine(contentRoot, "appsettings.json"),
            """
            {
              "Gateway": {
                "Bootstrap": { "Enabled": false },
                "ModelsConfigPath": "config/models.json",
                "Cors": { "AllowedOrigins": [] }
              }
            }
            """);

        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            configureSettings: settings =>
            {
                settings["Gateway:AppSettingsPath"] = "appsettings.json";
                settings["ASPNETCORE_ENVIRONMENT"] = Environments.Production;
            })
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.UseEnvironment(Environments.Production);
            });
    }

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}
