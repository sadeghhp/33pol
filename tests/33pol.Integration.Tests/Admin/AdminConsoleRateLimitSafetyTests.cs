using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Rate-limit safety contracts preserved in the Solid bundle, CSS, and help content.
/// </summary>
public sealed class AdminConsoleRateLimitSafetyTests
{
    [Fact]
    public async Task TheConsole_SaysWhatARefusalLooksLike()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var ratelimits = await AdminAssetTestHelpers.GetRatelimitsChunkAsync(client);
        ratelimits.Should().Contain("429");
        ratelimits.Should().Contain("Retry-After");

        var helpResponse = await client.GetAsync("/admin/admin-rate-limit-help.js");
        if (helpResponse.IsSuccessStatusCode)
        {
            var help = await helpResponse.Content.ReadAsStringAsync();
            help.Should().Contain("Nothing is queued or slowed");
        }
    }

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
    public async Task RateLimitsPage_LoadsRulesFromTheAdminApi()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var ratelimits = await AdminAssetTestHelpers.GetRatelimitsChunkAsync(client);

        ratelimits.Should().Contain("/admin/api/rate-limits");
    }
}
