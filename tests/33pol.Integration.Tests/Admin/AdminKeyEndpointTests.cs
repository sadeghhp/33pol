using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

[Trait("Category", "V1Parity")]
public sealed class AdminKeyEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    private const string InferenceRequestBody = @"{""model"":""gpt-local"",""stream"":false}";

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

    /// <summary>
    /// A past expiry is the caller's mistake. It used to escape the handler as an ArgumentException,
    /// be recorded as a gateway error, and come back as a 502 upstream_error.
    /// </summary>
    [Fact]
    public async Task PostKey_WithPastExpiry_Returns400WithMessage()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/keys",
            new { role = "Inference", expiresAt = DateTimeOffset.UtcNow.AddDays(-1) });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: body);
        body.Should().Contain("expiresAt must be in the future");
    }

    [Fact]
    public async Task PatchKey_WithPastExpiry_Returns400WithMessage()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var created = await client.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("id").GetGuid();

        var response = await client.PatchAsJsonAsync(
            $"/admin/api/keys/{id}",
            new { updateExpiry = true, expiresAt = DateTimeOffset.UtcNow.AddDays(-1) });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: body);
        body.Should().Contain("expiresAt must be in the future");
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

    // ---------------------------------------------------------------------------------------------
    // Archive
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArchiveKey_WhileActive_Returns409()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var key = await CreateInferenceKeyAsync(client);

        var response = await client.PostAsync($"/admin/api/keys/{key.Id}/archive", content: null);

        // Archiving a live credential would hide the key an operator most needs to see.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("code").GetString().Should().Be("key_not_revoked");
    }

    [Fact]
    public async Task ArchiveKey_HidesItFromTheDefaultListAndKeepsItsUsage()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var key = await CreateInferenceKeyAsync(client);
        await SeedUsageAsync(factory, key.Id);

        (await client.PostAsync($"/admin/api/keys/{key.Id}/revoke", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PostAsync($"/admin/api/keys/{key.Id}/archive", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ListKeyIdsAsync(client)).Should().NotContain(key.Id);

        var archived = (await ListKeysAsync(client, "?includeArchived=true&includeUsageSummary=true"))
            .Single(k => k.GetProperty("id").GetGuid() == key.Id);
        archived.GetProperty("isArchived").GetBoolean().Should().BeTrue();
        archived.GetProperty("hasUsage").GetBoolean().Should().BeTrue();
        archived.GetProperty("canDelete").GetBoolean().Should().BeFalse();
        archived.GetProperty("usageSummary").GetProperty("requestCount").GetInt32()
            .Should().Be(1, "archiving preserves the usage record, that is its purpose");
    }

    [Fact]
    public async Task UnarchiveKey_BringsItBackStillRevoked()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var key = await CreateInferenceKeyAsync(client);
        await client.PostAsync($"/admin/api/keys/{key.Id}/revoke", content: null);
        await client.PostAsync($"/admin/api/keys/{key.Id}/archive", content: null);

        (await client.PostAsync($"/admin/api/keys/{key.Id}/unarchive", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var restored = (await ListKeysAsync(client)).Single(k => k.GetProperty("id").GetGuid() == key.Id);
        restored.GetProperty("isArchived").GetBoolean().Should().BeFalse();
        restored.GetProperty("isRevoked").GetBoolean()
            .Should().BeTrue("unarchiving files a key back into view, it does not revive the credential");
    }

    // ---------------------------------------------------------------------------------------------
    // Delete
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeleteKey_WithoutAuth_Returns401()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);
        var key = await CreateInferenceKeyAsync(admin);

        var response = await factory.CreateClient().SendAsync(DeleteRequest(key.Id, "sk-33pol-anything"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteKey_WithInferenceKey_Returns403()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var admin = CreateAuthenticatedClient(factory, AdminKey);

        var target = await CreateInferenceKeyAsync(admin);
        var caller = await CreateInferenceKeyAsync(admin);

        var response = await CreateAuthenticatedClient(factory, caller.Secret)
            .SendAsync(DeleteRequest(target.Id, "sk-33pol-anything"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteKey_NeverUsed_Returns204AndKeepsTheHistory()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var key = await CreateInferenceKeyAsync(client);
        var prefix = await GetKeyPrefixAsync(client, key.Id);

        (await client.PostAsync($"/admin/api/keys/{key.Id}/revoke", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await client.SendAsync(DeleteRequest(key.Id, prefix));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListKeyIdsAsync(client, "?includeArchived=true")).Should().NotContain(key.Id);

        // Gone from the key table, but the history remains — that is the whole point of the design.
        var lifecycle = await client.GetAsync($"/admin/api/keys/{key.Id}/lifecycle");
        lifecycle.EnsureSuccessStatusCode();
        using var history = JsonDocument.Parse(await lifecycle.Content.ReadAsStringAsync());
        history.RootElement.GetProperty("exists").GetBoolean().Should().BeFalse();
        history.RootElement.GetProperty("status").GetString().Should().Be("deleted");
        history.RootElement.GetProperty("keyPrefix").GetString().Should().Be(prefix);
        history.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("event").GetString())
            .Should().Equal(["Created", "Revoked", "Deleted"]);
    }

    [Fact]
    public async Task DeleteKey_WithUsageHistory_Returns409AndKeepsTheKey()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var key = await CreateInferenceKeyAsync(client);
        var prefix = await GetKeyPrefixAsync(client, key.Id);
        await SeedUsageAsync(factory, key.Id);

        (await client.PostAsync($"/admin/api/keys/{key.Id}/revoke", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await client.SendAsync(DeleteRequest(key.Id, prefix));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("code").GetString().Should().Be("key_has_usage");
        json.RootElement.GetProperty("billingEventCount").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("message").GetString().Should().Contain("Archive it instead");

        (await ListKeyIdsAsync(client)).Should().Contain(key.Id);
    }

    [Fact]
    public async Task DeleteKey_WhileStillActive_Returns409()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var key = await CreateInferenceKeyAsync(client);
        var prefix = await GetKeyPrefixAsync(client, key.Id);

        var response = await client.SendAsync(DeleteRequest(key.Id, prefix));

        // Revoke-first closes the window in which the key could serve its first request between the
        // eligibility check and the delete.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("code").GetString().Should().Be("key_not_revoked");
    }

    [Fact]
    public async Task DeleteKey_WithWrongPrefixConfirmation_Returns400()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var key = await CreateInferenceKeyAsync(client);
        (await client.PostAsync($"/admin/api/keys/{key.Id}/revoke", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await client.SendAsync(DeleteRequest(key.Id, "sk-33pol-nope"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ListKeyIdsAsync(client)).Should().Contain(key.Id);
    }

    [Fact]
    public async Task DeleteKey_ThenPresentingTheSecret_LooksLikeAKeyThatNeverExisted()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var key = await CreateInferenceKeyAsync(client);
        var prefix = await GetKeyPrefixAsync(client, key.Id);
        await client.PostAsync($"/admin/api/keys/{key.Id}/revoke", content: null);
        (await client.SendAsync(DeleteRequest(key.Id, prefix)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var inference = factory.CreateClient();
        inference.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        using var body = new StringContent(
            InferenceRequestBody, System.Text.Encoding.UTF8, "application/json");
        var response = await inference.PostAsync("/v1/chat/completions", body);

        // Over the wire a deleted key looks like any other unusable one; that the gateway classifies it
        // as never-issued rather than withdrawn — which is what reaches the negative cache and what
        // anonymous-capable routes branch on — is pinned by
        // ApiKeyValidatorTests.ValidateAsync_DeletedKey_IsInvalidRatherThanRevoked.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetLifecycle_ForAKeyThisTenantNeverHad_Returns404()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.GetAsync($"/admin/api/keys/{Guid.NewGuid()}/lifecycle");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    private static HttpRequestMessage DeleteRequest(Guid keyId, string confirmKeyPrefix) =>
        new(HttpMethod.Delete, $"/admin/api/keys/{keyId}")
        {
            Content = JsonContent.Create(new { confirmKeyPrefix }),
        };

    private static async Task<IReadOnlyList<JsonElement>> ListKeysAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/admin/api/keys" + query);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static async Task<IReadOnlyList<Guid>> ListKeyIdsAsync(HttpClient client, string query = "") =>
        (await ListKeysAsync(client, query)).Select(k => k.GetProperty("id").GetGuid()).ToList();

    private static async Task<string> GetKeyPrefixAsync(HttpClient client, Guid keyId) =>
        (await ListKeysAsync(client, "?includeArchived=true"))
            .Single(k => k.GetProperty("id").GetGuid() == keyId)
            .GetProperty("keyPrefix")
            .GetString()!;

    /// <summary>
    /// Writes a ledger row for the key. The gateway stamps <c>LastUsedAt</c> only once a billing event
    /// has been persisted, so a row here is what a used key actually looks like in the database.
    /// </summary>
    private static async Task SeedUsageAsync(WebApplicationFactory<Program> factory, Guid keyId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var apiKeys = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
        // Stamped with the key's own tenant so the per-tenant usage summaries pick the row up too,
        // not just the tenant-agnostic "has this key ever been used" probe.
        var tenantId = (await apiKeys.GetByIdAsync(keyId))!.TenantId;

        var billingEvents = scope.ServiceProvider.GetRequiredService<IBillingEventRepository>();
        await billingEvents.TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(),
            "req-" + Guid.NewGuid().ToString("N"),
            tenantId,
            keyId,
            "gpt-local",
            "eng",
            10,
            5,
            null,
            null,
            0.10m,
            100,
            DateTimeOffset.UtcNow));
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
