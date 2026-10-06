using System.Text.RegularExpressions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Wallboard mode styles and entry points in the Solid admin bundle.
/// </summary>
public sealed class AdminWallboardAssetTests
{
    [Fact]
    public async Task Wallboard_ScalesTheRootRatherThanASingleFigure()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await AdminAssetTestHelpers.GetMainCssAsync(client);

        var rootRule = Regex.Match(css, @"html\.wallboard\s*\{[^}]*\}");
        rootRule.Success.Should().BeTrue("the wallboard must set a root type scale");
        rootRule.Value.Should().MatchRegex(
            @"font-size:\s*clamp\(",
            "a fixed root size reads too small on a 4K panel and too large on a laptop preview");
    }

    [Fact]
    public async Task Wallboard_SaysWhenItsFiguresAreNoLongerCurrent()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await AdminAssetTestHelpers.GetMainCssAsync(client);
        var overview = await AdminAssetTestHelpers.GetLazyChunkAsync(client, "OverviewPage");

        css.Should().Contain(".wallboard-stale-band");
        css.Should().Contain("html.wallboard-stale");
        overview.Should().Contain("wallboard");
        overview.Should().Contain("document.documentElement");
        overview.Should().Contain("wallboard-stale-band");
        overview.Should().Contain("wb-clock");
    }

    [Fact]
    public async Task Wallboard_HidesDeskControlsViaStylesheet()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await AdminAssetTestHelpers.GetMainCssAsync(client);

        css.Should().Contain("html.wallboard .wb-hide");
    }

    [Fact]
    public async Task Wallboard_TrimsTheLiveTailByColumnAndDropsItsFixedLayout()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await AdminAssetTestHelpers.GetMainCssAsync(client);

        css.Should().Contain("html.wallboard .t-requests{table-layout:auto");
        css.Should().Contain("html.wallboard .t-requests colgroup{display:none");
        css.Should().Contain("html.wallboard .t-requests tbody:nth-of-type(n+11){display:none");

        foreach (var column in new[] { 1, 3, 4, 6, 10, 11 })
        {
            css.Should().Contain(
                $"html.wallboard .t-requests td:nth-child({column})",
                $"column {column} must leave the board");
        }
    }

    [Fact]
    public async Task Wallboard_ShiftsPixelsForBurnInAndRespectsReducedMotion()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await AdminAssetTestHelpers.GetMainCssAsync(client);

        css.Should().Contain("@keyframes wallboard-shift");
        css.Should().Contain("html.wallboard .page-content{animation:wallboard-shift");
        css.Should().Contain("steps(1) infinite");

        css.Should().Contain("@media(prefers-reduced-motion:reduce)");
        css.Should().Contain("html.wallboard .page-content{animation:none");
    }

    [Fact]
    public async Task Wallboard_OffersAnEntryPointFromOverview()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var overview = await AdminAssetTestHelpers.GetLazyChunkAsync(client, "OverviewPage");

        overview.Should().Contain("Wallboard");
    }
}
