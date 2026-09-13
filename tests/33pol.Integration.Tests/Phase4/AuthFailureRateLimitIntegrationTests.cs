using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase4;

/// <summary>
/// The auth-failure budget only works from one place in the pipeline — outside the security
/// middleware, so it sees the status that middleware settled on — which nothing but an end-to-end
/// test can confirm.
/// </summary>
public sealed class AuthFailureRateLimitIntegrationTests
{
    private const string AdminKey = "sk-33pol-authfail-admin-key";

    private static readonly string ChatBody = JsonSerializer.Serialize(new
    {
        model = "local-mock",
        messages = new[] { new { role = "user", content = "hi" } },
    });

    [Fact]
    public async Task ChatCompletions_WithWrongKey_IsRateLimitedAfterItsBudgetIsSpent()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-33pol-not-a-real-key");

        (await PostChatAsync(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var refused = await PostChatAsync(client);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.Contains("Retry-After").Should().BeTrue();
        var payload = await refused.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("error").GetProperty("code").GetString().Should().Be("rate_limit_exceeded");
    }

    [Fact]
    public async Task AdminApi_WithoutAKey_IsRateLimitedAfterItsBudgetIsSpent()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        (await client.GetAsync("/admin/api/rate-limits")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/admin/api/rate-limits")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// The budget is per client address, and that is the whole of the blast radius: one address
    /// burning its allowance on wrong keys must leave every other address alone. Which address the
    /// gateway sees is what <c>ForwardedHeaders</c> decides — behind an ingress that is not
    /// configured, every caller shares one, and one guesser's spent budget is felt by all of them.
    /// </summary>
    [Fact]
    public async Task Guessing_FromOneAddress_LeavesAnotherAddressesBudgetIntact()
    {
        await using var factory = CreateFactory(trustForwardedHeaders: true);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var guesser = factory.CreateClient();
        guesser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-33pol-not-a-real-key");
        guesser.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

        (await PostChatAsync(guesser)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostChatAsync(guesser)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var innocent = factory.CreateClient();
        innocent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-33pol-also-not-real");
        innocent.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.8");

        (await PostChatAsync(innocent)).StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized, "a different address has a budget of its own");
    }

    /// <summary>A successful call is metered against its tenant, never against the guessing budget.</summary>
    [Fact]
    public async Task SuccessfulAdminCalls_DoNotSpendTheAuthFailureBudget()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

        for (var i = 0; i < 5; i++)
        {
            (await admin.GetAsync("/admin/api/rate-limits")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The budget of one is still intact, so the first wrong key is answered 401 rather than 429.
        var guesser = factory.CreateClient();
        guesser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-33pol-not-a-real-key");
        (await PostChatAsync(guesser)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A spent budget refuses only what cannot prove a credential. Refusing a good key too made a
    /// shared address — an ingress without ForwardedHeaders, a corporate NAT — a lockout for every
    /// caller behind it, the operator's admin access included, for as long as one stale key kept
    /// being retried.
    /// </summary>
    [Fact]
    public async Task SpentBudget_AValidKeyStillAuthenticates()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var guesser = factory.CreateClient();
        guesser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-33pol-not-a-real-key");
        (await guesser.GetAsync("/admin/api/rate-limits")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await guesser.GetAsync("/admin/api/rate-limits")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Same (absent) address as the guesser in the test host.
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        (await admin.GetAsync("/admin/api/rate-limits")).StatusCode.Should()
            .Be(HttpStatusCode.OK, "a key that validates is never locked out by someone else's guessing");

        (await guesser.GetAsync("/admin/api/rate-limits")).StatusCode.Should()
            .Be(HttpStatusCode.TooManyRequests, "the valid key's pass-through spends nothing, so the guesser stays refused");
    }

    /// <summary>
    /// A 403 for a model the key was never granted is a recognised credential being told no, not a
    /// guess. Charging it let any valid key lock its own address out of the gateway by asking for the
    /// wrong model a few dozen times.
    /// </summary>
    [Fact]
    public async Task GrantDeniedModel_DoesNotSpendTheAuthFailureBudget()
    {
        // A roomy tenant tier, so the refusals under test are the router's 403s and not the
        // tenant's own 1 rpm budget.
        await using var factory = CreateFactory(defaultRpm: 100);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        var ungranted = await CreateInferenceClientAsync(factory, admin, grantLocalMock: false);

        for (var i = 0; i < 3; i++)
        {
            (await PostChatAsync(ungranted)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        // The budget of one is still intact, so the first wrong key is answered 401 rather than 429.
        var guesser = factory.CreateClient();
        guesser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-33pol-not-a-real-key");
        (await PostChatAsync(guesser)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The forwarder copies the upstream's status onto the response. An upstream whose own credential
    /// has expired answers 401 to everyone; charging those spent every client address's budget on an
    /// outage none of them caused, and locked the operator out of fixing it.
    /// </summary>
    [Fact]
    public async Task UpstreamUnauthorized_DoesNotSpendTheAuthFailureBudget()
    {
        await using var factory = CreateFactory(
            upstreamHandler: new FixedStatusUpstreamHandler(HttpStatusCode.Unauthorized),
            defaultRpm: 100);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        var client = await CreateInferenceClientAsync(factory, admin, grantLocalMock: true);

        // Two, not more: enough to prove the charge is not happening without nearing the circuit
        // breaker's failure threshold, which would change the status under test.
        for (var i = 0; i < 2; i++)
        {
            (await PostChatAsync(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the upstream's 401 is passed through");
        }

        var guesser = factory.CreateClient();
        guesser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-33pol-not-a-real-key");
        (await PostChatAsync(guesser)).StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized, "an upstream's 401 is not a guessed credential and spends nothing");
    }

    private static async Task<HttpClient> CreateInferenceClientAsync(
        WebApplicationFactory<Program> factory,
        HttpClient admin,
        bool grantLocalMock)
    {
        var createKey = await admin.PostAsJsonAsync("/admin/api/keys", new { role = "Inference" });
        createKey.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createKey.Content.ReadAsStringAsync());
        var keyId = created.RootElement.GetProperty("id").GetGuid();
        var secret = created.RootElement.GetProperty("secret").GetString()!;

        if (grantLocalMock)
        {
            var grant = await admin.PutAsJsonAsync(
                $"/admin/api/keys/{keyId}/model-grants",
                new { modelIds = new[] { "local-mock" } });
            grant.EnsureSuccessStatusCode();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private sealed class FixedStatusUpstreamHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    """{"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private static async Task<HttpResponseMessage> PostChatAsync(HttpClient client)
    {
        using var content = new StringContent(ChatBody, Encoding.UTF8, "application/json");
        return await client.PostAsync("/v1/chat/completions", content);
    }

    /// <summary>
    /// A guessing budget of exactly one request, so a single rejection spends it.
    /// </summary>
    /// <remarks>
    /// Set explicitly rather than inherited from the default tier. These tests used to lower
    /// <c>Default</c> alone and rely on the auth-failure tier falling through to it — which it did,
    /// because the configured <c>AuthFailure</c> tier was never seeded into the database and so was
    /// never in force. Now that it is, the fall-through no longer happens and the budget under test
    /// has to be the one the test names.
    /// </remarks>
    private static WebApplicationFactory<Program> CreateFactory(
        bool trustForwardedHeaders = false,
        HttpMessageHandler? upstreamHandler = null,
        int defaultRpm = 1) =>
        GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            upstreamHandler: upstreamHandler,
            configureSettings: settings =>
            {
                // A private local-mock, so a key without a grant is refused 403 by the router.
                IntegrationModelsConfig.ApplyStandardModelsSettings(
                    settings,
                    IntegrationModelsConfig.WriteStandardModelsConfig());

                settings["RateLimiting:Default:Rpm"] = defaultRpm.ToString();
                settings["RateLimiting:Default:Burst"] = "0";
                settings["RateLimiting:AuthFailure:Rpm"] = "1";
                settings["RateLimiting:AuthFailure:Burst"] = "0";

                if (trustForwardedHeaders)
                {
                    // The test host gives every connection the same (absent) address, so the only
                    // way to have two callers is the header a real deployment would get from its
                    // ingress.
                    settings["Gateway:ForwardedHeaders:Enabled"] = "true";
                    settings["Gateway:ForwardedHeaders:TrustAllProxies"] = "true";
                }
            });
}
