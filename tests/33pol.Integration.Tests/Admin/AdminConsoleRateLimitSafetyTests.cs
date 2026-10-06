using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Rate-limit safety contracts: server constants, CSS invariants, and bundle delivery.
/// Help semantics and UI behaviour are covered by Vitest and Playwright E2E.
/// </summary>
public sealed class AdminConsoleRateLimitSafetyTests
{
    [Fact]
    public async Task ConsoleLimits_MirrorTheServerConstants()
    {
        RateLimitConfigValidation.MaxRpm.Should().Be(1_000_000);
        RateLimitConfigValidation.MaxBurst.Should().Be(1_000_000);
        RateLimitConfigValidation.MaxMaxConcurrentStreams.Should().Be(10_000);
        RateLimitScopeNames.IsRateOnly(RateLimitScopeNames.AuthFailure).Should().BeTrue();
    }

    [Fact]
    public async Task ASwitchedOffRule_ReadsAsOffRatherThanAsItsTier()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await AdminAssetTestHelpers.GetMainCssAsync(client);

        css.Should().Contain(".rl-row.off .rl-limit-nums b");
        css.Should().Contain(".rl-row.off .rl-model");
        css.Should().NotContain(".rl-row.off td { opacity");
    }

    [Fact]
    public async Task RatelimitsChunk_IsPreloadedFromIndexHtml()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);
        var path = AdminAssetTestHelpers.ExtractRatelimitsChunkPath(html);
        var chunk = await AdminAssetTestHelpers.GetAssetTextAsync(client, path);

        chunk.Length.Should().BeGreaterThan(1000, "ratelimits chunk must ship in the build output");
    }
}
