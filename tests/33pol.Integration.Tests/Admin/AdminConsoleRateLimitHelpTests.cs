using System.Net;
using System.Text.RegularExpressions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Rate-limit help content: bundled in the ratelimits chunk and/or copied to a standalone file by
/// postbuild for CI check scripts.
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
    public async Task HelpModule_CarriesEveryScopeInBothLanguages()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var js = await GetHelpContentAsync(client);

        js.Should().Contain("RateLimitHelp");
        js.Should().Contain("{ id: 'en', label: 'EN', name: 'English', dir: 'ltr' }");
        js.Should().Contain("{ id: 'fa', label: 'فا', name: 'فارسی', dir: 'rtl' }");

        foreach (var scope in ScopeIds)
        {
            Regex.Matches(js, $@"^\s+{Regex.Escape(scope)}: \{{$", RegexOptions.Multiline).Count
                .Should().BeGreaterThanOrEqualTo(2, $"scope '{scope}' needs entries under both languages");
        }
    }

    [Fact]
    public async Task HelpContent_ExplainsRefusalSemantics()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var js = await GetHelpContentAsync(client);

        js.Should().Contain("429");
        js.Should().Contain("Retry-After");
        js.Should().Contain("Nothing is queued or slowed");
    }

    private static async Task<string> GetHelpContentAsync(HttpClient client)
    {
        // Solid loads help at runtime from the standalone file copied by postbuild; the ratelimits
        // chunk only references RateLimitHelp in fetch/parse code, not the full guide prose.
        var standalone = await client.GetAsync("/admin/admin-rate-limit-help.js");
        standalone.StatusCode.Should().Be(HttpStatusCode.OK, "help must be copied by postbuild");
        var text = await standalone.Content.ReadAsStringAsync();
        text.Should().Contain("RateLimitHelp");
        return text;
    }
}
