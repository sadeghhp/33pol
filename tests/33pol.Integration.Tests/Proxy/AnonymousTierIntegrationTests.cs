using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Proxy;

/// <summary>
/// Anonymous traffic to a <c>publicAccess</c> model is held to the <c>anonymous</c> tier, not the
/// default one. Before the tier existed every anonymous address received the tier sized for a paying
/// tenant — thousands of requests a minute — and nothing smaller could be configured without also
/// tightening every authenticated tenant on the default tier.
/// </summary>
public sealed class AnonymousTierIntegrationTests
{
    private const string AdminKey = "sk-33pol-anonymous-tier-admin-key";
    private const string ModelId = "local-mock";

    [Fact]
    public async Task AnonymousCallers_AreHeldToTheAnonymousTier()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var anonymous = factory.CreateClient();

        (await PostChatAsync(anonymous)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostChatAsync(anonymous)).StatusCode.Should().Be(HttpStatusCode.OK);

        var refused = await PostChatAsync(anonymous);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.GetValues("X-33pol-RateLimit-Limit").Single().Should()
            .Be("2", "the anonymous tier, not the default tier, is the budget");
        refused.Headers.GetValues("X-33pol-RateLimit-Scope").Single().Should().Be("tenant");
    }

    /// <summary>A key that validates on the same address is metered by its own tenant tier as before.</summary>
    [Fact]
    public async Task AuthenticatedCallers_KeepTheirTenantTier()
    {
        await using var factory = CreateFactory();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var anonymous = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            (await PostChatAsync(anonymous)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await PostChatAsync(anonymous)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var authenticated = factory.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

        var admitted = await PostChatAsync(authenticated);

        admitted.StatusCode.Should().Be(HttpStatusCode.OK, "the anonymous tier binds anonymous callers only");
        admitted.Headers.GetValues("X-33pol-RateLimit-Limit").Single().Should().Be("10000");
    }

    private static async Task<HttpResponseMessage> PostChatAsync(HttpClient client)
    {
        using var content = new StringContent(
            $$"""{"model":"{{ModelId}}","stream":false}""",
            Encoding.UTF8,
            "application/json");
        return await client.PostAsync("/v1/chat/completions", content);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var configPath = WritePublicModelConfig();
        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            upstreamHandler: new MockUpstreamHandler(),
            configureSettings: settings =>
            {
                IntegrationModelsConfig.ApplyStandardModelsSettings(settings, configPath);

                settings["RateLimiting:Enabled"] = "true";
                settings["RateLimiting:Default:Rpm"] = "10000";
                settings["RateLimiting:Default:Burst"] = "0";
                settings["RateLimiting:Anonymous:Rpm"] = "2";
                settings["RateLimiting:Anonymous:Burst"] = "0";
                settings["RateLimiting:Anonymous:MaxConcurrentStreams"] = "0";
            });
    }

    private static string WritePublicModelConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"33pol-anonymous-tier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "models.json");
        const string json = """
            {
              "models": [
                {
                  "id": "local-mock",
                  "url": "http://127.0.0.1:18080",
                  "maxContextLength": 8192,
                  "aliases": [],
                  "publicAccess": true
                }
              ]
            }
            """;
        File.WriteAllText(path, json);
        return path;
    }
}
