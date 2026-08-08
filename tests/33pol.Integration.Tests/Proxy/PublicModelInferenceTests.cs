using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Proxy;

public sealed class PublicModelInferenceTests
{
    private const string AdminKey = "sk-33pol-public-model-admin";
    private const string ModelId = "local-mock";
    private const string ModelAlias = "gpt-local";
    private const string PrivateModelId = "other-mock";

    [Fact]
    public async Task PublicModel_NoApiKey_AllowsInference()
    {
        await using var factory = CreateFactoryWithPublicModel();
        var client = factory.CreateClient();

        using var body = ChatBody(ModelAlias);
        var response = await client.PostAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A key that was presented and rejected is an authentication failure, even on a public model.
    /// </summary>
    /// <remarks>
    /// Serving these anonymously answered 200 to a caller whose key had been revoked or had expired,
    /// so clients, CI checks and SDKs had no way to discover that their credential had stopped
    /// working — the failure looked exactly like success. Omitting the key entirely still works;
    /// that is the case <c>publicAccess</c> exists for.
    /// </remarks>
    [Fact]
    public async Task PublicModel_GarbageApiKey_ReturnsUnauthorized()
    {
        await using var factory = CreateFactoryWithPublicModel();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-not-a-real-key");

        using var body = ChatBody(ModelAlias);
        var response = await client.PostAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PrivateModel_NoApiKey_ReturnsUnauthorized()
    {
        await using var factory = CreateFactoryWithPublicModel();
        var client = factory.CreateClient();

        using var body = ChatBody(PrivateModelId);
        var response = await client.PostAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublicModel_ValidKeyWithoutGrant_AllowsInference()
    {
        await using var factory = CreateFactoryWithPublicModel();
        var adminClient = CreateAuthenticatedClient(factory, AdminKey);
        var createResponse = await adminClient.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createResponse.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        var inferenceClient = factory.CreateClient();
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        using var body = ChatBody(ModelAlias);
        var response = await inferenceClient.PostAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetModels_NoApiKey_ReturnsOnlyPublicModels()
    {
        await using var factory = CreateFactoryWithPublicModel();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/models");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();

        ids.Should().ContainSingle().Which.Should().Be(ModelId);
    }

    private static WebApplicationFactory<Program> CreateFactoryWithPublicModel()
    {
        var configPath = WritePublicModelsConfig();
        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            upstreamHandler: new MockUpstreamHandler(),
            configureSettings: settings =>
                IntegrationModelsConfig.ApplyStandardModelsSettings(settings, configPath));
    }

    private static string WritePublicModelsConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"33pol-public-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "models.json");
        const string json = """
            {
              "models": [
                {
                  "id": "local-mock",
                  "url": "http://127.0.0.1:18080",
                  "maxContextLength": 8192,
                  "aliases": ["mock", "gpt-local"],
                  "publicAccess": true
                },
                {
                  "id": "other-mock",
                  "url": "http://127.0.0.1:18080",
                  "maxContextLength": 8192,
                  "aliases": []
                }
              ]
            }
            """;
        File.WriteAllText(path, json);
        return path;
    }

    private static StringContent ChatBody(string model) =>
        new(
            $$"""{"model":"{{model}}","stream":false}""",
            System.Text.Encoding.UTF8,
            "application/json");

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}
