using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Stopping a model route from the admin panel: the reversible half of "take this out of service".
/// A stopped route keeps its aliases, credential, pricing and grants, but the gateway stops
/// advertising it on <c>GET /v1/models</c> and stops accepting inference for it.
/// </summary>
public sealed class AdminModelStopEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";
    private const string ModelId = "local-mock";
    private const string ModelAlias = "gpt-local";

    [Fact]
    public async Task StopModel_MovesStateFromServingToStopped()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var admin = CreateAdminClient(factory);

        (await GetAdminModelStateAsync(admin, ModelId)).Should().Be("serving");

        var stop = await admin.PostAsync(StopUrl(ModelId), content: null);
        stop.StatusCode.Should().Be(HttpStatusCode.OK, await stop.Content.ReadAsStringAsync());

        (await GetAdminModelStateAsync(admin, ModelId)).Should().Be("stopped");
    }

    /// <summary>Stopping is not deleting: the route stays in the admin inventory so it can come back.</summary>
    [Fact]
    public async Task StopModel_KeepsTheRouteRegistered_AndStartPutsItBack()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var admin = CreateAdminClient(factory);

        (await admin.PostAsync(StopUrl(ModelId), null)).EnsureSuccessStatusCode();

        var models = await ListAdminModelsAsync(admin);
        models.Select(m => m.GetProperty("model").GetProperty("id").GetString())
            .Should().Contain(ModelId, "a stopped route is still registered");

        var start = await admin.PostAsync(StartUrl(ModelId), null);
        start.StatusCode.Should().Be(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());

        (await GetAdminModelStateAsync(admin, ModelId)).Should().Be("serving");
    }

    [Fact]
    public async Task StopModel_ByAlias_StopsTheOwningRoute()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var admin = CreateAdminClient(factory);

        (await admin.PostAsync(StopUrl(ModelAlias), null)).EnsureSuccessStatusCode();

        (await GetAdminModelStateAsync(admin, ModelId)).Should().Be("stopped");
    }

    [Fact]
    public async Task StoppedModel_DisappearsFromModelsEndpoint()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var admin = CreateAdminClient(factory);
        using var caller = await CreateGrantedCallerAsync(factory, admin, ModelId, "other-mock");

        (await ListServedModelIdsAsync(caller)).Should().Contain(ModelId);

        (await admin.PostAsync(StopUrl(ModelId), null)).EnsureSuccessStatusCode();

        var served = await ListServedModelIdsAsync(caller);
        served.Should().NotContain(ModelId);
        served.Should().Contain("other-mock", "stopping one route must not hide the others");

        // And it comes back when the operator starts it again.
        (await admin.PostAsync(StartUrl(ModelId), null)).EnsureSuccessStatusCode();
        (await ListServedModelIdsAsync(caller)).Should().Contain(ModelId);
    }

    /// <summary>
    /// The per-model lookup has to agree with the listing, or a client that resolves a model id
    /// before using it would be told the model exists and then be refused on first request.
    /// </summary>
    [Theory]
    [InlineData(ModelId)]
    [InlineData(ModelAlias)]
    public async Task StoppedModel_GetByIdOrAlias_Returns404(string name)
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var admin = CreateAdminClient(factory);
        using var caller = await CreateGrantedCallerAsync(factory, admin, ModelId);

        (await caller.GetAsync("/v1/models/" + name)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PostAsync(StopUrl(ModelId), null)).EnsureSuccessStatusCode();

        (await caller.GetAsync("/v1/models/" + name)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The requirement that matters most: a caller who could use the model a moment ago cannot use
    /// it once it is stopped — by canonical id or by alias — and can again once it is started.
    /// </summary>
    [Theory]
    [InlineData(ModelId)]
    [InlineData(ModelAlias)]
    public async Task StoppedModel_CannotBeUsedForInference(string requestedModel)
    {
        var upstream = new MockUpstreamHandler();
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey, upstream);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var admin = CreateAdminClient(factory);

        var (keyId, secret) = await CreateInferenceKeyAsync(admin);
        await ModelGrantTestHelpers.GrantApiKeyModelsAsync(admin, keyId, ModelId);

        using var caller = factory.CreateClient();
        caller.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        (await PostInferenceAsync(caller, requestedModel)).StatusCode
            .Should().Be(HttpStatusCode.OK, "the model is serving to begin with");

        (await admin.PostAsync(StopUrl(ModelId), null)).EnsureSuccessStatusCode();

        var forwardsBefore = upstream.SendCount;
        var refused = await PostInferenceAsync(caller, requestedModel);

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await refused.Content.ReadAsStringAsync();
        using var error = JsonDocument.Parse(body);
        error.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("model_not_found");
        error.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Contain("stopped");

        upstream.SendCount.Should().Be(
            forwardsBefore,
            "a stopped model is refused at admission, so nothing reaches the upstream");

        (await admin.PostAsync(StartUrl(ModelId), null)).EnsureSuccessStatusCode();
        (await PostInferenceAsync(caller, requestedModel)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The admin drawer round-trips the whole model without a state field. An unrelated edit must
    /// therefore not quietly put a stopped route back into service.
    /// </summary>
    [Fact]
    public async Task EditingAStoppedModel_DoesNotPutItBackIntoService()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var admin = CreateAdminClient(factory);

        (await admin.PostAsync(StopUrl(ModelId), null)).EnsureSuccessStatusCode();

        var patch = await admin.PatchAsJsonAsync("/admin/api/models/" + ModelId, new
        {
            model = new
            {
                id = ModelId,
                url = "http://relocated.test",
                aliases = new[] { ModelAlias },
                maxContextLength = 8192,
            },
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());

        (await GetAdminModelStateAsync(admin, ModelId)).Should().Be("stopped");

        using var caller = await CreateGrantedCallerAsync(factory, admin, ModelId);
        (await ListServedModelIdsAsync(caller)).Should().NotContain(ModelId);
    }

    [Fact]
    public async Task StopModel_UnknownId_Returns404()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var admin = CreateAdminClient(factory);

        var response = await admin.PostAsync(StopUrl("no-such-model"), null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Taking a model out of service is an operator action, not an anonymous one.</summary>
    [Fact]
    public async Task StopModel_WithoutOperatorCredential_IsRefused_AndTheModelKeepsServing()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsync(StopUrl(ModelId), content: null);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        using var admin = CreateAdminClient(factory);
        (await GetAdminModelStateAsync(admin, ModelId)).Should().Be("serving");
    }

    private static async Task<HttpResponseMessage> PostInferenceAsync(HttpClient client, string model)
    {
        using var body = new StringContent(
            $$"""{"model":"{{model}}","stream":false,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");
        return await client.PostAsync("/v1/chat/completions", body);
    }

    /// <summary>
    /// An inference client granted the given models. The bootstrap admin key is deliberately not
    /// used for these assertions: it holds no model grants, so <c>/v1/models</c> is empty for it
    /// whatever the routes are doing, and it would make a stopped model look excluded for the
    /// wrong reason.
    /// </summary>
    private static async Task<HttpClient> CreateGrantedCallerAsync(
        WebApplicationFactory<Program> factory,
        HttpClient admin,
        params string[] modelIds)
    {
        var (keyId, secret) = await CreateInferenceKeyAsync(admin);
        await ModelGrantTestHelpers.GrantApiKeyModelsAsync(admin, keyId, modelIds);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private static async Task<(Guid Id, string Secret)> CreateInferenceKeyAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("id").GetGuid(), json.RootElement.GetProperty("secret").GetString()!);
    }

    private static string StopUrl(string id) => "/admin/api/models/" + Uri.EscapeDataString(id) + "/stop";

    private static string StartUrl(string id) => "/admin/api/models/" + Uri.EscapeDataString(id) + "/start";

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);
        return client;
    }

    private static async Task<JsonElement[]> ListAdminModelsAsync(HttpClient admin)
    {
        var response = await admin.GetAsync("/admin/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static async Task<string> GetAdminModelStateAsync(HttpClient admin, string modelId)
    {
        var models = await ListAdminModelsAsync(admin);
        var model = models
            .Single(m => m.GetProperty("model").GetProperty("id").GetString() == modelId)
            .GetProperty("model");
        return model.GetProperty("state").GetString()!;
    }

    private static async Task<string[]> ListServedModelIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/v1/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .Select(m => m.GetProperty("id").GetString()!)
            .ToArray();
    }
}
