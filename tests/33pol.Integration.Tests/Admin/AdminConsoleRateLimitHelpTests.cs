using System.Net;
using System.Text.RegularExpressions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The rate-limit help is a static content module plus markup, so nothing else in the suite would
/// notice if it were dropped, shipped in one language, or lost a scope: the console keeps working
/// and the operator loses the explanation. These pin the contract between the three files —
/// index.html references the module, the module carries both languages for every scope the wizard
/// offers, and the drawer takes part in the console's modal plumbing like every other surface.
/// </summary>
public sealed class AdminConsoleRateLimitHelpTests
{
    private static readonly string[] ScopeIds =
        ["model", "tenant", "api_key", "global", "tenant_model", "api_key_model", "anonymous", "auth_failure"];

    private static async Task<string> GetAssetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task AdminIndex_LoadsTheHelpModuleBeforeTheApp()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetAssetAsync(client, "/admin/index.html");

        var help = Regex.Match(html, "<script defer src=\"admin-rate-limit-help\\.js\\?v=\\d+\"></script>");
        var app = Regex.Match(html, "<script defer src=\"admin-app\\.js\\?v=\\d+\"></script>");
        help.Success.Should().BeTrue("the help module must be referenced with a cache-busting version");
        app.Success.Should().BeTrue();
        // Both are `defer`, which runs them in document order; the app reads window.RateLimitHelp
        // lazily, but keeping the content first means a page whose scripts finish in order has the
        // words available for the first render.
        help.Index.Should().BeLessThan(app.Index, "the content module must come before the app that reads it");
    }

    [Fact]
    public async Task HelpModule_CarriesEveryScopeInBothLanguages()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var js = await GetAssetAsync(client, "/admin/admin-rate-limit-help.js");

        js.Should().Contain("window.RateLimitHelp = {");
        js.Should().Contain("{ id: 'en', label: 'EN', name: 'English', dir: 'ltr' }");
        js.Should().Contain("{ id: 'fa', label: 'فا', name: 'فارسی', dir: 'rtl' }");

        foreach (var scope in ScopeIds)
        {
            Regex.Matches(js, $@"^\s+{Regex.Escape(scope)}: \{{$", RegexOptions.Multiline).Count
                .Should().Be(2, $"scope '{scope}' needs an entry under both `en.scopes` and `fa.scopes`");
        }

        // One section id list per language, in the same order: the drawer's table of contents and
        // the "?" buttons address sections by id, in whichever language is showing.
        var ids = Regex.Matches(js, @"^\s+id: '([a-z]+)',$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        ids.Count.Should().BeGreaterThan(0);
        (ids.Count % 2).Should().Be(0, "every section must exist in both languages");
        ids.Take(ids.Count / 2).Should().Equal(ids.Skip(ids.Count / 2), "the Persian sections must mirror the English ones, in order");
    }

    [Fact]
    public async Task HelpDrawer_IsWiredIntoTheModalPlumbingAndOpensAboveTheOtherDrawers()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetAssetAsync(client, "/admin/index.html");
        var js = await GetAssetAsync(client, "/admin/admin-app.js");
        var css = await GetAssetAsync(client, "/admin/admin.css");

        // The drawer exists, is a dialog, and follows the selected language's direction.
        html.Should().Contain("class=\"drawer-backdrop rl-help-backdrop\" x-show=\"rlHelpOpen\"");
        html.Should().Contain("role=\"dialog\" aria-modal=\"true\" aria-labelledby=\"rl-help-title\" :dir=\"rlHelpView.dir\" :lang=\"rlHelpView.lang\"");

        // It is the first dialog in the DOM, because the focus trap adopts the first visible one and
        // the help may open over the rule, tier or new-rule drawer.
        var helpIndex = html.IndexOf("rl-help-backdrop", StringComparison.Ordinal);
        var ruleIndex = html.IndexOf("x-show=\"rlRuleDrawerOpen\"", StringComparison.Ordinal);
        helpIndex.Should().BePositive();
        helpIndex.Should().BeLessThan(ruleIndex, "the help drawer must precede the rate-limit drawers in the DOM");
        css.Should().Contain(".rl-help-backdrop { z-index: 101; }", "it must also paint above the other backdrops, which are z-index 100");

        // Escape closes it first, and it counts as an open modal for the focus trap.
        js.Should().Contain("else if (this.rlHelpOpen) this.closeRateLimitHelp();");
        js.Should().Contain("return !!(this.confirmDialog || this.rlHelpOpen || this.rlWindowOpen");

        // The language switch is remembered like the theme.
        js.Should().Contain("localStorage.setItem('33pol-admin-help-lang', id);");
        js.Should().Contain("rlHelpLang: localStorage.getItem('33pol-admin-help-lang') || 'en',");
    }

    [Fact]
    public async Task EveryEditingSurface_OffersHelpAndALanguageSwitch()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetAssetAsync(client, "/admin/index.html");

        // The page and each drawer explain the numbers where they are typed.
        html.Should().Contain("x-text=\"rlHelpView.f.numbers.title\"");
        html.Should().Contain("x-text=\"rlHelpView.f.ruleNumbers.title\"");
        html.Should().Contain("x-text=\"rlHelpView.f.tiers.title\"");
        html.Should().Contain("x-text=\"rlHelpView.f.planSlug.title\"");
        html.Should().Contain("x-text=\"rlHelpView.f.windowKind.title\"");
        html.Should().Contain("x-text=\"rlHelpView.f.suspend.title\"");
        html.Should().Contain("x-text=\"rlHelpView.f.priority.title\"");
        html.Should().Contain("x-text=\"rlHelpView.f.target.title\"");
        html.Should().Contain("x-text=\"rlHelpView.f.save.title\"");

        // The wizard explains the highlighted scope before the operator commits to it.
        html.Should().Contain("class=\"rl-help-scope\" :dir=\"rlHelpView.dir\" :lang=\"rlHelpView.lang\"");
        html.Should().Contain("x-text=\"rlHelpView.scope.what\"");

        // Every inline explainer carries an example and a way to switch language on the spot.
        // Since the page redesign they live where a value is being typed — the drawers, plus one beside
        // the change review — and the page's own sections reach the guide through a "?" instead, so
        // the rule list is not buried under a dozen disclosure boxes. The floor keeps every drawer's
        // explainer; the ceiling is what stops them creeping back onto the page.
        var blocks = Regex.Matches(html, "<details class=\"rl-help\"").Count;
        blocks.Should().BeInRange(9, 12);
        html.Should().Contain("@click=\"rlHelpView.open.combine\"");
        html.Should().Contain("@click=\"rlHelpView.open.calendar\"");
        Regex.Matches(html, "x-text=\"rlHelpView\\.f\\.[a-zA-Z]+\\.example\"").Count.Should().Be(blocks);
        Regex.Matches(html, "@click=\"toggleRateLimitHelpLang\"").Count.Should().BeGreaterThanOrEqualTo(blocks);

        // Each drawer header has a "?" that opens the guide at the matching section.
        html.Should().Contain("@click=\"rlHelpView.open.windows\"");
        html.Should().Contain("@click=\"rlHelpView.open.tiers\"");
        html.Should().Contain("@click=\"rlHelpView.open.scopes\"");
        html.Should().Contain("@click=\"rlHelpView.open.overview\"");
    }
}
