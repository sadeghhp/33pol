using System.Net;
using System.Text.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Rate-limit help content published as static JSON (runtime fetch) and JS (build/check scripts).
/// </summary>
public sealed class AdminConsoleRateLimitHelpTests
{
    private static readonly string[] ScopeIds =
        ["model", "tenant", "api_key", "global", "tenant_model", "api_key_model", "anonymous", "auth_failure"];

    [Fact]
    public async Task AdminIndex_PreloadsTheRatelimitsChunk()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);

        html.Should().MatchRegex(@"<link[^>]+rel=""modulepreload""[^>]+href=""/admin/assets/ratelimits-[^""]+\.js""");
        html.Should().MatchRegex(@"<script[^>]+type=""module""[^>]+src=""/admin/assets/index-[^""]+\.js""");
    }

    [Fact]
    public async Task HelpJson_IsPublishedForRuntimeFetch()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var json = await client.GetAsync("/admin/admin-rate-limit-help.json");
        json.StatusCode.Should().Be(HttpStatusCode.OK, "help JSON must be emitted by postbuild");
        var text = await json.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);

        doc.RootElement.TryGetProperty("en", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("fa", out _).Should().BeTrue();

        foreach (var scope in ScopeIds)
        {
            text.Should().Contain($"\"{scope}\"");
        }
    }

    [Fact]
    public async Task HelpJs_IsCopiedForCheckScripts()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/admin-rate-limit-help.js");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "help JS must be copied by postbuild");
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("RateLimitHelp");
    }
}
