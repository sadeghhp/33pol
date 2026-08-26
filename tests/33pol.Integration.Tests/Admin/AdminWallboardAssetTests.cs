using System.Net;
using System.Text.RegularExpressions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The wallboard is the one console mode nobody is watching from a keyboard: it runs unattended on
/// a NOC panel for weeks, so the things that make it safe to leave alone are exactly the things no
/// operator is present to notice have regressed. These lock down the properties a silent break
/// would cost — legible type, a board that says when it is stale, no destructive control left on
/// screen, and no query running for a card that is hidden.
/// </summary>
public sealed class AdminWallboardAssetTests
{
    private static async Task<string> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// The whole mode hangs on one lever: every size in the console is a rem against the token
    /// scale, so scaling the root scales labels, tables, chips and meters together. Enlarging one
    /// figure instead — which is what this mode used to do — leaves everything around it at
    /// desk-reading size on a screen nobody is sitting at.
    /// </summary>
    [Fact]
    public async Task Wallboard_ScalesTheRootRatherThanASingleFigure()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await GetAsync(client, "/admin/admin.css");

        var rootRule = Regex.Match(css, @"html\.wallboard\s*\{[^}]*\}");
        rootRule.Success.Should().BeTrue("the wallboard must set a root type scale");
        rootRule.Value.Should().MatchRegex(
            @"font-size:\s*clamp\(",
            "a fixed root size reads too small on a 4K panel and too large on a laptop preview");
    }

    /// <summary>
    /// A board showing figures that stopped updating is worse than a blank one, because it looks
    /// authoritative. The band and the drained figures are the only thing that distinguishes them.
    /// </summary>
    [Fact]
    public async Task Wallboard_SaysWhenItsFiguresAreNoLongerCurrent()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetAsync(client, "/admin/index.html");
        var css = await GetAsync(client, "/admin/admin.css");
        var app = await GetAsync(client, "/admin/admin-app.js");

        html.Should().Contain("class=\"wallboard-stale-band\" x-show=\"wallboardStale\"");
        html.Should().Contain("x-text=\"wallboardStaleTitle\"");
        html.Should().Contain("x-text=\"wallboardStaleText\"");
        css.Should().Contain(".wallboard-stale-band");
        css.Should().Contain("html.wallboard-stale #panel-dashboard >");

        // A rejected key and a failed refresh both stop the data, so both have to count as stale —
        // not just the age of the last successful update.
        var stale = Regex.Match(app, @"get wallboardStale\(\)\s*\{.*?\n    \},", RegexOptions.Singleline);
        stale.Success.Should().BeTrue();
        stale.Value.Should().Contain("connectionStatus");
        stale.Value.Should().Contain("overviewStale");
        stale.Value.Should().Contain("WALLBOARD_STALE_MS");
    }

    /// <summary>
    /// "Clear errors" wipes the gateway's recorded errors. On an unattended screen in a shared room
    /// it is one stray click from a passer-by, and the wallboard hides every other way to undo it.
    /// </summary>
    [Fact]
    public async Task Wallboard_LeavesNoDestructiveControlOnScreen()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetAsync(client, "/admin/index.html");
        var css = await GetAsync(client, "/admin/admin.css");

        css.Should().Contain("html.wallboard .wb-hide");

        // The block holding "Clear errors" must carry the marker the wallboard hides.
        var clearErrors = html.IndexOf("confirmClearErrors", StringComparison.Ordinal);
        clearErrors.Should().BeGreaterThan(0);
        var enclosing = html.LastIndexOf("<div class=\"card-head-actions", clearErrors, StringComparison.Ordinal);
        enclosing.Should().BeGreaterThan(0);
        html[enclosing..clearErrors].Should().Contain("wb-hide");
    }

    /// <summary>
    /// Pause lives in the filter row the wallboard hides, so a feed frozen at a desk and then put on
    /// a wall would sit there with nothing saying it is frozen and nothing able to release it.
    /// Filters are kept instead of cleared — a board pinned to one model is a real setup — which is
    /// only defensible because the narrowing is stated on the board.
    /// </summary>
    [Fact]
    public async Task Wallboard_ResumesAPausedFeedAndDeclaresAnyTailFilter()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetAsync(client, "/admin/index.html");
        var app = await GetAsync(client, "/admin/admin-app.js");

        var prepare = Regex.Match(app, @"    prepareWallboard\(\)\s*\{.*?\n    \},", RegexOptions.Singleline);
        prepare.Success.Should().BeTrue();
        prepare.Value.Should().Contain("requestsPaused");
        prepare.Value.Should().Contain("toggleRequestsPause");

        html.Should().Contain("class=\"wb-filter\" x-show=\"hasWallboardFilters\" x-text=\"wallboardFilterText\"");
    }

    /// <summary>
    /// Four of the five database-backed sections are hidden by the wallboard, and a screen left up
    /// for a month would otherwise run their queries every thirty seconds for nothing.
    /// </summary>
    [Fact]
    public async Task Wallboard_DoesNotPollTheSectionsItHides()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var app = await GetAsync(client, "/admin/admin-app.js");

        var loaders = Regex.Match(app, @"    overviewSlowLoaders\(\)\s*\{.*?\n    \},", RegexOptions.Singleline);
        loaders.Success.Should().BeTrue();

        var wallboardBranch = Regex.Match(loaders.Value, @"if \(this\.wallboard\) return \[(?<body>[^\]]*)\]");
        wallboardBranch.Success.Should().BeTrue("the wallboard must take a narrower set of loaders");
        var kept = wallboardBranch.Groups["body"].Value;
        kept.Should().Contain("loadOverviewPolicy", "policy pressure stays on the board");
        kept.Should().NotContain("loadOverviewFinops");
        kept.Should().NotContain("loadOverviewActivity");
        kept.Should().NotContain("loadOverviewTenants");
        kept.Should().NotContain("loadOverviewControlPlane");

        // Leaving the mode has to re-run the full set, or the cards come back holding whatever they
        // were showing when the board went up.
        var restore = Regex.Match(app, @"    restoreFromWallboard\(\)\s*\{.*?\n    \},", RegexOptions.Singleline);
        restore.Success.Should().BeTrue();
        restore.Value.Should().Contain("loadOverviewSlow");
    }

    /// <summary>
    /// The tail keeps six of its twelve columns, which only works if the fixed layout and the column
    /// shares chosen for twelve go with them — otherwise the six that remain are sized by a
    /// colgroup describing a different table.
    /// </summary>
    [Fact]
    public async Task Wallboard_TrimsTheLiveTailByColumnAndDropsItsFixedLayout()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await GetAsync(client, "/admin/admin.css");

        css.Should().Contain("html.wallboard .t-requests { table-layout: auto;");
        css.Should().Contain("html.wallboard .t-requests colgroup { display: none; }");
        css.Should().Contain("html.wallboard .t-requests tbody:nth-of-type(n+11) { display: none; }");

        // Request id, route, cost centre, TTFT and tok/s are desk columns; the pin is a desk control.
        foreach (var column in new[] { 1, 3, 4, 6, 10, 11 })
        {
            css.Should().MatchRegex(
                @"html\.wallboard \.t-requests td:nth-child\(" + column + @"\)",
                $"column {column} must leave the board");
        }
    }

    /// <summary>
    /// Esc reaches the wallboard through the same handler as every dialog, and last, so a confirm
    /// opened on top of a board closes on the first press and drops the board on the second — one
    /// key never doing two things at once.
    /// </summary>
    [Fact]
    public async Task Wallboard_ExitsOnEscapeBehindEveryDialog()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetAsync(client, "/admin/index.html");
        var app = await GetAsync(client, "/admin/admin-app.js");

        html.Should().NotContain("@keydown.escape.window=\"exitWallboard\"",
            "a window-level handler on an x-show'd panel fires from every other tab too");

        var handler = Regex.Match(app, @"    onModalKeydown\(e\)\s*\{.*?\n    \},", RegexOptions.Singleline);
        handler.Success.Should().BeTrue();
        handler.Value.Should().Contain("this.exitWallboard()");
        handler.Value.IndexOf("exitWallboard", StringComparison.Ordinal)
            .Should().BeGreaterThan(handler.Value.IndexOf("closeKeysDrawer", StringComparison.Ordinal),
                "dialogs must consume Escape before the wallboard does");
    }

    /// <summary>
    /// A display that sleeps is not a wallboard, and the browser drops a screen wake lock every time
    /// the document is hidden without ever giving it back.
    /// </summary>
    [Fact]
    public async Task Wallboard_HoldsTheScreenAwakeAndRetakesTheLockAfterAHide()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var app = await GetAsync(client, "/admin/admin-app.js");

        app.Should().Contain("navigator.wakeLock.request('screen')");

        var visibility = Regex.Match(
            app, @"addEventListener\('visibilitychange'.*?\n      \}\);", RegexOptions.Singleline);
        visibility.Success.Should().BeTrue();
        visibility.Value.Should().Contain("syncWallboardEffects",
            "a lock dropped while the tab was hidden is never restored on its own");

        // The hint is the only place an operator learns whether the lock actually took.
        app.Should().Contain("get wallboardHintText()");
    }

    /// <summary>
    /// Static bright figures, on the same subpixels, for the length of a deployment. The shift is
    /// deliberately below the threshold of notice and holds each position rather than sliding, and
    /// it must not run for someone who asked for no motion.
    /// </summary>
    [Fact]
    public async Task Wallboard_ShiftsPixelsForBurnInAndRespectsReducedMotion()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await GetAsync(client, "/admin/admin.css");

        css.Should().Contain("@keyframes wallboard-shift");
        css.Should().MatchRegex(@"html\.wallboard \.page-content \{ animation: wallboard-shift \d+s steps\(1\) infinite; \}");

        var reduced = Regex.Match(css, @"@media \(prefers-reduced-motion: reduce\) \{[^}]*html\.wallboard \.page-content \{ animation: none; \}");
        reduced.Success.Should().BeTrue("the pixel shift must be opt-out for reduced motion");
    }

    /// <summary>
    /// Dismissals and the collapse toggle are gestures made by someone who could see the item. A
    /// board that quietly folds its alerts away — or drops them because of a click made hours ago in
    /// the same browser profile — is worse than no board.
    /// </summary>
    [Fact]
    public async Task Wallboard_ShowsEveryAttentionItemExpanded()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var app = await GetAsync(client, "/admin/admin-app.js");
        var css = await GetAsync(client, "/admin/admin.css");

        app.Should().Contain("get attentionExpanded() { return !this.attentionCollapsed || this.wallboard; }");
        app.Should().Contain("this.wallboard || !this.attentionDismissed.includes(key)");

        // Per-item Open/Dismiss and the collapse toggle are keyboard controls with no keyboard.
        css.Should().Contain("html.wallboard .attention-actions");
        css.Should().Contain("html.wallboard .attention-head .action");
    }
}
