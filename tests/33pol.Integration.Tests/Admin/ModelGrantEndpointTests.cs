using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class ModelGrantEndpointTests
{
    private const string AdminKey = "sk-33pol-model-grant-admin";
    private const string CanonicalModelId = "local-mock";
    private const string RequestModelAlias = "gpt-local";
    private const string OtherRegistryModel = "other-mock";

    [Fact]
    public async Task PutApiKeyGrants_ThenInference_DeniesUnlistedModel()
    {
        await using var factory = CreateGrantTestFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var adminClient = CreateAuthenticatedClient(factory, AdminKey);
        var createResponse = await adminClient.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createResponse.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var keyId = created.RootElement.GetProperty("id").GetGuid();
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        var putGrants = await adminClient.PutAsJsonAsync(
            $"/admin/api/keys/{keyId}/model-grants",
            new { modelIds = new[] { CanonicalModelId } });
        putGrants.EnsureSuccessStatusCode();

        var inferenceClient = factory.CreateClient();
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        using var allowedBody = new StringContent(
            $$"""{"model":"{{RequestModelAlias}}","stream":false}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var allowed = await inferenceClient.PostAsync("/v1/chat/completions", allowedBody);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var deniedBody = new StringContent(
            $$"""{"model":"{{OtherRegistryModel}}","stream":false}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var denied = await inferenceClient.PostAsync("/v1/chat/completions", deniedBody);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        denied.Headers.GetValues("X-33pol-Error-Code").Single().Should().Be("insufficient_scope");
    }

    [Fact]
    public async Task PutTenantGrants_CapsKeyAllowlist()
    {
        await using var factory = CreateGrantTestFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var adminClient = CreateAuthenticatedClient(factory, AdminKey);
        var tenantPut = await adminClient.PutAsJsonAsync(
            "/admin/api/tenant/model-grants",
            new { modelIds = new[] { CanonicalModelId } });
        tenantPut.EnsureSuccessStatusCode();

        var createResponse = await adminClient.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createResponse.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var keyId = created.RootElement.GetProperty("id").GetGuid();
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        var keyPut = await adminClient.PutAsJsonAsync(
            $"/admin/api/keys/{keyId}/model-grants",
            new { modelIds = new[] { OtherRegistryModel } });
        keyPut.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var inferenceClient = factory.CreateClient();
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var deniedBody = new StringContent(
            $$"""{"model":"{{RequestModelAlias}}","stream":false}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var denied = await inferenceClient.PostAsync("/v1/chat/completions", deniedBody);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await ModelGrantTestHelpers.GrantApiKeyModelsAsync(adminClient, keyId, CanonicalModelId);
        using var allowedBody = new StringContent(
            $$"""{"model":"{{RequestModelAlias}}","stream":false}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var allowed = await inferenceClient.PostAsync("/v1/chat/completions", allowedBody);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NewInferenceKey_WithoutGrants_DeniesModelsAndInference()
    {
        await using var factory = CreateGrantTestFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var adminClient = CreateAuthenticatedClient(factory, AdminKey);
        var createResponse = await adminClient.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createResponse.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        var inferenceClient = factory.CreateClient();
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var modelsResponse = await inferenceClient.GetAsync("/v1/models");
        modelsResponse.EnsureSuccessStatusCode();
        using var modelsDoc = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
        modelsDoc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);

        using var chatBody = new StringContent(
            $$"""{"model":"{{RequestModelAlias}}","stream":false}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var chat = await inferenceClient.PostAsync("/v1/chat/completions", chatBody);
        chat.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetModels_WithRestrictedKey_ReturnsSubset()
    {
        await using var factory = CreateGrantTestFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var adminClient = CreateAuthenticatedClient(factory, AdminKey);
        var createResponse = await adminClient.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createResponse.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var keyId = created.RootElement.GetProperty("id").GetGuid();
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        await adminClient.PutAsJsonAsync(
            $"/admin/api/keys/{keyId}/model-grants",
            new { modelIds = new[] { CanonicalModelId } });

        var inferenceClient = factory.CreateClient();
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        var modelsResponse = await inferenceClient.GetAsync("/v1/models");
        modelsResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();
        ids.Should().ContainSingle().Which.Should().Be(CanonicalModelId);
    }

    private static WebApplicationFactory<Program> CreateGrantTestFactory()
    {
        var configPath = IntegrationModelsConfig.WriteStandardModelsConfig();
        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            upstreamHandler: new MockUpstreamHandler(),
            configureSettings: settings =>
                IntegrationModelsConfig.ApplyStandardModelsSettings(settings, configPath));
    }

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}
