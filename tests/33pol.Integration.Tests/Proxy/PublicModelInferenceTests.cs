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
    /// A key the gateway never issued is treated as no key at all on a public model.
    /// </summary>
    /// <remarks>
    /// This is the normal case, not an edge case: OpenAI-compatible SDKs refuse to construct a
    /// client with an empty api_key, so virtually every caller of a public model sends a
    /// placeholder. Rejecting them left <c>publicAccess</c> reachable only by bare curl.
    /// </remarks>
    [Theory]
    [InlineData("lm-studio")]
    [InlineData("not-needed")]
    [InlineData("sk-not-a-real-key")]
    public async Task PublicModel_PlaceholderApiKey_AllowsInference(string placeholder)
    {
        await using var factory = CreateFactoryWithPublicModel();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", placeholder);

        using var body = ChatBody(ModelAlias);
        var response = await client.PostAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A key the gateway <em>did</em> issue and has since revoked still fails on a public model.
    /// </summary>
    /// <remarks>
    /// Serving it anonymously answered 200 to a caller whose credential had been withdrawn, so
    /// clients, CI checks and SDKs had no way to discover that it had stopped working — the failure
    /// looked exactly like success. This is the case the placeholder allowance must not swallow.
    /// </remarks>
    [Fact]
    public async Task PublicModel_RevokedApiKey_ReturnsUnauthorized()
    {
        await using var factory = CreateFactoryWithPublicModel();
        var adminClient = CreateAuthenticatedClient(factory, AdminKey);
        var (secret, keyId) = await CreateInferenceKeyAsync(adminClient);

        var revokeResponse = await adminClient.PostAsync($"/admin/api/keys/{keyId}/revoke", content: null);
        revokeResponse.EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

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
        var (secret, _) = await CreateInferenceKeyAsync(adminClient);

        var inferenceClient = factory.CreateClient();
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        using var body = ChatBody(ModelAlias);
        var response = await inferenceClient.PostAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetModels_NoApiKey_ListsAllModelsAndFlagsWhichNeedKey()
    {
        await using var factory = CreateFactoryWithPublicModel();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/models");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var byId = doc.RootElement.GetProperty("data").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("requires_api_key").GetBoolean());

        byId.Should().ContainKey(ModelId).WhoseValue.Should().BeFalse();
        byId.Should().ContainKey(PrivateModelId).WhoseValue.Should().BeTrue();
        doc.RootElement.GetProperty("help").GetString().Should().Contain("Authorization: Bearer");
    }

    /// <summary>
    /// Model discovery has to work for the same placeholder-key clients that inference serves,
    /// otherwise an SDK configured with a dummy key cannot find the models it is allowed to call.
    /// </summary>
    [Fact]
    public async Task GetModels_PlaceholderApiKey_ListsAllModelsAndFlagsWhichNeedKey()
    {
        await using var factory = CreateFactoryWithPublicModel();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "lm-studio");

        var response = await client.GetAsync("/v1/models");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var byId = doc.RootElement.GetProperty("data").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("requires_api_key").GetBoolean());

        byId.Should().ContainKey(ModelId).WhoseValue.Should().BeFalse();
        byId.Should().ContainKey(PrivateModelId).WhoseValue.Should().BeTrue();
    }

    [Fact]
    public async Task GetModels_RevokedApiKey_ReturnsUnauthorized()
    {
        await using var factory = CreateFactoryWithPublicModel();
        var adminClient = CreateAuthenticatedClient(factory, AdminKey);
        var (secret, keyId) = await CreateInferenceKeyAsync(adminClient);
        (await adminClient.PostAsync($"/admin/api/keys/{keyId}/revoke", content: null)).EnsureSuccessStatusCode();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<(string Secret, string KeyId)> CreateInferenceKeyAsync(HttpClient adminClient)
    {
        var createResponse = await adminClient.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createResponse.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        return (
            created.RootElement.GetProperty("secret").GetString()!,
            created.RootElement.GetProperty("id").GetString()!);
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
