using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Runs against a real SQLite database (migrations applied) rather than the EF InMemory provider,
/// so collation, constraints and query translation are exercised the way production sees them.
/// </summary>
public sealed class AdminModelPricingTests
{
    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");
        return client;
    }

    private static async Task<JsonElement?> FindModelAsync(HttpClient client, string modelId)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/admin/api/models");
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.TryGetProperty("model", out var m) &&
                m.TryGetProperty("id", out var id) &&
                id.GetString() == modelId)
            {
                return entry;
            }
        }

        return null;
    }

    [Fact]
    public async Task PostModel_WithPricing_PersistsAndReturnsIt()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory);

        var modelId = "priced-" + Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/admin/api/models", new
        {
            model = new { id = modelId, url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 },
            pricing = new { inputPricePerMillionTokens = 3.00, outputPricePerMillionTokens = 15.00 }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = await FindModelAsync(client, modelId);
        entry.Should().NotBeNull();
        var pricing = entry!.Value.GetProperty("pricing");
        pricing.GetProperty("inputPricePerMillionTokens").GetDecimal().Should().Be(3.00m);
        pricing.GetProperty("outputPricePerMillionTokens").GetDecimal().Should().Be(15.00m);
        pricing.GetProperty("currency").GetString().Should().Be("USD");
    }

    [Fact]
    public async Task PatchModel_UpdatesPricingInPlace()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory);

        var modelId = "repriced-" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/admin/api/models", new
        {
            model = new { id = modelId, url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 },
            pricing = new { inputPricePerMillionTokens = 3.00, outputPricePerMillionTokens = 15.00 }
        });

        var patch = await client.PatchAsJsonAsync("/admin/api/models/" + Uri.EscapeDataString(modelId), new
        {
            model = new { id = modelId, url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 },
            pricing = new { inputPricePerMillionTokens = 5.50, outputPricePerMillionTokens = 22.00 }
        });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = await FindModelAsync(client, modelId);
        entry!.Value.GetProperty("pricing").GetProperty("inputPricePerMillionTokens").GetDecimal().Should().Be(5.50m);
    }

    [Fact]
    public async Task PatchModel_WithClearPricing_LeavesModelUnpriced()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory);

        var modelId = "unpriced-" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/admin/api/models", new
        {
            model = new { id = modelId, url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 },
            pricing = new { inputPricePerMillionTokens = 3.00, outputPricePerMillionTokens = 15.00 }
        });

        await client.PatchAsJsonAsync("/admin/api/models/" + Uri.EscapeDataString(modelId), new
        {
            model = new { id = modelId, url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 },
            clearPricing = true
        });

        var entry = await FindModelAsync(client, modelId);
        entry!.Value.TryGetProperty("pricing", out var pricing).Should().BeTrue();
        pricing.ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// The registry resolves model ids case-insensitively, so pricing set under different casing
    /// must still apply. Before the NOCASE collation and id canonicalisation this wrote a second
    /// rate card that the model never matched, leaving it silently unpriced.
    /// </summary>
    [Fact]
    public async Task PatchModel_PricingWithDifferentlyCasedId_StillAppliesToTheModel()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory);

        var modelId = "cased-" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/admin/api/models", new
        {
            model = new { id = modelId, url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 }
        });

        var upper = modelId.ToUpperInvariant();
        var patch = await client.PatchAsJsonAsync("/admin/api/models/" + Uri.EscapeDataString(upper), new
        {
            model = new { id = upper, url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 },
            pricing = new { inputPricePerMillionTokens = 3.00, outputPricePerMillionTokens = 15.00 }
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        // Listed under the canonical id, with the price attached.
        var entry = await FindModelAsync(client, modelId);
        entry.Should().NotBeNull();
        entry!.Value.GetProperty("pricing").GetProperty("inputPricePerMillionTokens").GetDecimal().Should().Be(3.00m);
    }

    [Fact]
    public async Task PostModel_PricingForUnregisteredModel_DoesNotSilentlySucceed()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory);

        // Pricing is applied after the model is persisted, so the model here does exist by then.
        // This guards the inverse: the endpoint must not accept pricing for a model that never
        // made it into the registry.
        var response = await client.PostAsJsonAsync("/admin/api/models", new
        {
            model = new { id = "", url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 },
            pricing = new { inputPricePerMillionTokens = 3.00, outputPricePerMillionTokens = 15.00 }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostModel_WithNegativePrice_Returns400()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory);

        var response = await client.PostAsJsonAsync("/admin/api/models", new
        {
            model = new { id = "bad-price-model", url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 },
            pricing = new { inputPricePerMillionTokens = -1.0, outputPricePerMillionTokens = 15.00 }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Guards the allow-list rebuild in AdminModelProvisioningService.PrepareModel and
    /// ModelRegistryPersistence.CloneModel: fields not named there were silently dropped,
    /// which is how capabilities were being lost on every update.
    /// </summary>
    [Fact]
    public async Task PatchModel_PreservesCapabilitiesAndPricing_AcrossUnrelatedUpdate()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory);

        var modelId = "roundtrip-" + Guid.NewGuid().ToString("N")[..8];
        var created = await client.PostAsJsonAsync("/admin/api/models", new
        {
            model = new
            {
                id = modelId,
                url = "https://openrouter.ai/api",
                aliases = new[] { modelId + "-alias" },
                maxContextLength = 8192,
                capabilities = new[] { "chat", "embeddings" }
            },
            pricing = new { inputPricePerMillionTokens = 3.00, outputPricePerMillionTokens = 15.00 }
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        // Change only the context length, echoing capabilities back as the admin UI does.
        var patch = await client.PatchAsJsonAsync("/admin/api/models/" + Uri.EscapeDataString(modelId), new
        {
            model = new
            {
                id = modelId,
                url = "https://openrouter.ai/api",
                aliases = new[] { modelId + "-alias" },
                maxContextLength = 16384,
                capabilities = new[] { "chat", "embeddings" }
            }
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = await FindModelAsync(client, modelId);
        entry.Should().NotBeNull();

        var model = entry!.Value.GetProperty("model");
        model.GetProperty("maxContextLength").GetInt32().Should().Be(16384);
        model.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString())
            .Should().BeEquivalentTo("chat", "embeddings");

        // Pricing was not part of the patch, so it must survive untouched.
        entry.Value.GetProperty("pricing").GetProperty("inputPricePerMillionTokens").GetDecimal().Should().Be(3.00m);
    }

    [Fact]
    public async Task DeleteModel_AlsoClearsPricing()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAdminClient(factory);

        var modelId = "recreated-" + Guid.NewGuid().ToString("N")[..8];
        var model = new { id = modelId, url = "https://openrouter.ai/api", aliases = Array.Empty<string>(), maxContextLength = 8192 };

        await client.PostAsJsonAsync("/admin/api/models", new
        {
            model,
            pricing = new { inputPricePerMillionTokens = 3.00, outputPricePerMillionTokens = 15.00 }
        });

        var delete = await client.DeleteAsync("/admin/api/models/" + Uri.EscapeDataString(modelId));
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        // Re-create under the same id; it must not inherit the old rate card.
        await client.PostAsJsonAsync("/admin/api/models", new { model });

        var entry = await FindModelAsync(client, modelId);
        entry!.Value.TryGetProperty("pricing", out var pricing).Should().BeTrue();
        pricing.ValueKind.Should().Be(JsonValueKind.Null);
    }
}
