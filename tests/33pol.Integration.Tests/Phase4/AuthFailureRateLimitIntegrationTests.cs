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

    private static async Task<HttpResponseMessage> PostChatAsync(HttpClient client)
    {
        using var content = new StringContent(ChatBody, Encoding.UTF8, "application/json");
        return await client.PostAsync("/v1/chat/completions", content);
    }

    /// <summary>
    /// A budget of exactly one request, so a single rejection spends it. Rate limits for the admin
    /// key's own traffic stay generous, which is what separates the two budgets in these tests.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(bool trustForwardedHeaders = false) =>
        GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            configureSettings: settings =>
            {
                settings["RateLimiting:Default:Rpm"] = "1";
                settings["RateLimiting:Default:Burst"] = "0";

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
