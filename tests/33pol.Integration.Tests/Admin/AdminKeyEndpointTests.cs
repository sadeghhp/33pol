using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

[Trait("Category", "V1Parity")]
public sealed class AdminKeyEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    [Fact]
    public async Task PostKey_WithoutAuth_Returns401()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/admin/api/keys",
            new { role = "Inference" });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, because: body);
    }

    [Fact]
    public async Task PostKey_WithAdminKey_ReturnsSecretOnce()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/keys",
            new { role = "Inference" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("secret").GetString().Should().StartWith("sk-33pol-");

        var listResponse = await client.GetAsync("/admin/api/keys");
        listResponse.EnsureSuccessStatusCode();
        var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listJson.RootElement.EnumerateArray().Should().NotBeEmpty();
        listJson.RootElement[0].TryGetProperty("secret", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RevokeKey_SubsequentInference_Returns401()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var adminClient = CreateAuthenticatedClient(factory, AdminKey);

        var createResponse = await adminClient.PostAsJsonAsync(
            "/admin/api/keys",
            new { role = "Inference" });
        createResponse.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var keyId = created.RootElement.GetProperty("id").GetGuid();
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        var revokeResponse = await adminClient.PostAsync($"/admin/api/keys/{keyId}/revoke", content: null);
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var inferenceClient = factory.CreateClient();
        inferenceClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var body = new StringContent("""{"model":"gpt-local","stream":false}""", System.Text.Encoding.UTF8, "application/json");
        var inferenceResponse = await inferenceClient.PostAsync("/v1/chat/completions", body);

        inferenceResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokeKeysBatch_SubsequentInferenceWithAllKeys_Returns401()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var adminClient = CreateAuthenticatedClient(factory, AdminKey);

        var firstKey = await CreateInferenceKeyAsync(adminClient);
        var secondKey = await CreateInferenceKeyAsync(adminClient);

        var response = await adminClient.PostAsJsonAsync(
            "/admin/api/keys/revoke",
            new { keyIds = new[] { firstKey.Id, secondKey.Id } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("revokedCount").GetInt32().Should().Be(2);

        var firstInference = factory.CreateClient();
        firstInference.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstKey.Secret);
        using var firstBody = new StringContent("""{"model":"gpt-local","stream":false}""", System.Text.Encoding.UTF8, "application/json");
        var firstResponse = await firstInference.PostAsync("/v1/chat/completions", firstBody);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var secondInference = factory.CreateClient();
        secondInference.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondKey.Secret);
        using var secondBody = new StringContent("""{"model":"gpt-local","stream":false}""", System.Text.Encoding.UTF8, "application/json");
        var secondResponse = await secondInference.PostAsync("/v1/chat/completions", secondBody);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokeKeysBatch_WithoutIds_Returns400()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/keys/revoke",
            new { keyIds = Array.Empty<Guid>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostConfigReload_WithoutAuth_Returns401()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.PostAsync("/admin/api/config/reload", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static async Task<(Guid Id, string Secret)> CreateInferenceKeyAsync(HttpClient adminClient)
    {
        var response = await adminClient.PostAsJsonAsync(
            "/admin/api/keys",
            new { role = "Inference" });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            json.RootElement.GetProperty("id").GetGuid(),
            json.RootElement.GetProperty("secret").GetString()!);
    }
}
