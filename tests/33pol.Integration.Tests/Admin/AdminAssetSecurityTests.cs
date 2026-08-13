using System.Net;
using System.Text.RegularExpressions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The admin console handles API keys, upstream provider secrets and pricing. It must therefore
/// serve every asset from this origin — a CDN compromise would otherwise execute arbitrary script
/// in a fully-privileged admin session — and it must work with no internet access at all, which the
/// Docker/on-prem deployments require.
/// </summary>
public sealed class AdminAssetSecurityTests
{
    private static async Task<string> GetIndexAsync(HttpClient client)
    {
        var response = await client.GetAsync("/admin/index.html");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task AdminIndex_ReferencesNoExternalScriptsOrStylesheets()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetIndexAsync(client);

        var externalRefs = Regex
            .Matches(html, @"<(?:script|link)[^>]*(?:src|href)=""(https?://[^""]+)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .ToList();

        externalRefs.Should().BeEmpty(
            "admin assets must be self-hosted; found: " + string.Join(", ", externalRefs));
    }

    [Fact]
    public async Task AdminIndex_DoesNotPreconnectToThirdPartyOrigins()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetIndexAsync(client);

        html.Should().NotContain("fonts.googleapis.com");
        html.Should().NotContain("fonts.gstatic.com");
        html.Should().NotContain("cdn.jsdelivr.net");
    }

    [Theory]
    [InlineData("/admin/vendor/alpine-csp-3.14.9.min.js")]
    [InlineData("/admin/vendor/fonts.css")]
    [InlineData("/admin/vendor/fonts/IBMPlexSans-400.woff2")]
    [InlineData("/admin/vendor/fonts/IBMPlexMono-400.woff2")]
    [InlineData("/admin/vendor/fonts/SpaceGrotesk-500.woff2")]
    public async Task VendoredAssets_AreServedLocally(string path)
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Every asset the page actually references must resolve locally — otherwise the console breaks
    /// in an air-gapped deployment even though no external URL appears in the markup.
    /// </summary>
    [Fact]
    public async Task EveryReferencedAsset_ResolvesFromThisOrigin()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetIndexAsync(client);

        var refs = Regex
            .Matches(html, @"<(?:script|link)[^>]*(?:src|href)=""(?!data:|https?://)([^""]+)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        refs.Should().NotBeEmpty();

        foreach (var reference in refs)
        {
            var response = await client.GetAsync("/admin/" + reference.TrimStart('/'));
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{reference} must be served locally");
        }
    }

    [Fact]
    public async Task AdminAssets_CarryARestrictiveContentSecurityPolicy()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/index.html");

        response.Headers.TryGetValues("Content-Security-Policy", out var values).Should().BeTrue();
        var csp = string.Join(" ", values!);

        csp.Should().Contain("default-src 'self'");
        csp.Should().Contain("script-src 'self'");
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().Contain("object-src 'none'");

        // Inline script is where the injection risk lies and stays fully blocked. Inline *style* is
        // allowed because Alpine's x-show writes style="display:none" at runtime.
        csp.Should().NotContain("script-src 'self' 'unsafe-inline'");
        csp.Should().NotContain("unsafe-eval");
    }

    /// <summary>
    /// The console keeps <c>script-src 'self'</c> only because it runs on Alpine's CSP-friendly
    /// build, whose evaluator resolves a directive's value as a property path instead of compiling
    /// it with <c>new Function()</c>. An expression with an operator, a ternary or a call therefore
    /// does not merely look different — it silently fails to evaluate at runtime. This test is the
    /// tripwire: it fails on the markup rather than in an operator's browser console.
    /// </summary>
    [Fact]
    public async Task AdminIndex_UsesOnlyExpressionsTheCspEvaluatorCanResolve()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = Regex.Replace(await GetIndexAsync(client), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        // A bare property path, dotted or hyphenated: `summary`, `mdl.editModel.url`, `icons.bar-chart`.
        var path = @"[A-Za-z_$][A-Za-z0-9_$\-]*(?:\.[A-Za-z0-9_$\-]+)*";
        var pathOnly = new Regex($"^{path}$");
        // x-for takes one extra shape the evaluator parses itself: `item in <path>`.
        var forExpression = new Regex($@"^\(?\s*[A-Za-z_$][A-Za-z0-9_$]*\s*(?:,\s*[A-Za-z_$][A-Za-z0-9_$]*\s*)?\)?\s+(?:in|of)\s+{path}$");

        var offenders = Regex
            .Matches(html, @"\s(?<name>(?:x-|@|:)[A-Za-z0-9_:.\-]*)=""(?<value>[^""]*)""")
            .Where(m =>
            {
                var value = m.Groups["value"].Value.Trim();
                if (value.Length == 0) return false;
                var name = m.Groups["name"].Value;
                // x-ref names an element; it is stored verbatim and never evaluated.
                if (name.StartsWith("x-ref", StringComparison.Ordinal)) return false;
                if (name.StartsWith("x-for", StringComparison.Ordinal)) return !forExpression.IsMatch(value);
                return !pathOnly.IsMatch(value);
            })
            .Select(m => m.Groups["name"].Value + "=\"" + m.Groups["value"].Value + "\"")
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            "the CSP-friendly Alpine build evaluates property paths only — move the logic into a getter, " +
            "a zero-argument method or a {get,set} pair on adminApp; found: " + string.Join(" | ", offenders));
    }

    /// <summary>
    /// Shape is not enough: <c>x-text="totalErrorsTxt"</c> is a perfectly well-formed path that
    /// resolves to nothing. Because the CSP evaluator fails silently — a warning in the operator's
    /// console and an empty binding — a renamed getter would otherwise ship green. This checks that
    /// the root of every bound path is either an x-for alias declared in the same markup or a member
    /// declared on adminApp.
    /// </summary>
    [Fact]
    public async Task AdminIndex_BindsOnlyToNamesDeclaredOnAdminApp()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = Regex.Replace(await GetIndexAsync(client), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
        var app = await client.GetStringAsync("/admin/admin-app.js");

        // Members of the object literal adminApp() returns: `foo:`, `foo()`, `get foo()`, `async foo()`.
        var declared = Regex
            .Matches(app, @"(?m)^\s{4}(?:async\s+|get\s+|set\s+)?(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*[:(]")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Loop aliases are scoped names, not members; x-data names the registered Alpine component.
        var locals = Regex
            .Matches(html, @"x-for=""\(?\s*(?<item>[A-Za-z_$][A-Za-z0-9_$]*)\s*(?:,\s*(?<index>[A-Za-z_$][A-Za-z0-9_$]*))?")
            .SelectMany(m => new[] { m.Groups["item"].Value, m.Groups["index"].Value })
            .Where(name => name.Length > 0)
            .Concat(Regex.Matches(html, @"x-data=""(?<name>[A-Za-z_$][A-Za-z0-9_$]*)""").Select(m => m.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var unresolved = Regex
            .Matches(html, @"\s(?<name>(?:x-|@|:)[A-Za-z0-9_:.\-]*)=""(?<value>[^""]*)""")
            .Where(m => !m.Groups["name"].Value.StartsWith("x-ref", StringComparison.Ordinal))
            .Select(m => new
            {
                Directive = m.Groups["name"].Value,
                // x-for's own `item in items` shape: only the collection is a path.
                Root = Regex.Replace(m.Groups["value"].Value.Trim(), @"^.*\s(?:in|of)\s+", string.Empty)
                    .Split('.')[0]
            })
            .Where(binding => binding.Root.Length > 0)
            .Where(binding => !declared.Contains(binding.Root) && !locals.Contains(binding.Root))
            .Select(binding => binding.Directive + " → " + binding.Root)
            .Distinct()
            .ToList();

        unresolved.Should().BeEmpty(
            "every bound path must start at an x-for alias or a member of adminApp, or the CSP " +
            "evaluator resolves it to undefined at runtime; found: " + string.Join(" | ", unresolved));
    }

    /// <summary>
    /// Alpine's <c>x-for</c> clones only the first element of its template. A row plus its
    /// expandable detail row is two <c>&lt;tr&gt;</c>, so the pair has to be wrapped in its own
    /// <c>&lt;tbody&gt;</c> or every detail row silently disappears.
    /// </summary>
    /// <remarks>
    /// This was a comment on three tables. Making it a test means the next expandable table cannot
    /// get it wrong without failing here first.
    /// </remarks>
    [Fact]
    public async Task AdminIndex_WrapsMultiRowLoopTemplatesInTheirOwnTbody()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = Regex.Replace(await GetIndexAsync(client), "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var offenders = Regex
            .Matches(html, @"<template\s+x-for=""(?<expr>[^""]*)""[^>]*>(?<body>.*?)</template>", RegexOptions.Singleline)
            .Where(m => Regex.Matches(m.Groups["body"].Value, "<tr[\\s>]").Count > 1)
            .Where(m => !Regex.IsMatch(m.Groups["body"].Value.TrimStart(), @"^<tbody[\s>]"))
            .Select(m => m.Groups["expr"].Value)
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            "an x-for template holding more than one <tr> must open with <tbody>, or Alpine clones "
            + "only the first row and the detail row never renders; found: " + string.Join(" | ", offenders));
    }

    [Fact]
    public async Task AdminIndex_DeclaresTheErrorsPanel()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetIndexAsync(client);

        html.Should().Contain("id=\"panel-errors\"");
        html.Should().Contain("x-show=\"isErrors\"");
        html.Should().Contain("aria-labelledby=\"tab-errors\"");
        html.Should().Contain("x-for=\"r in errorRows\"");
        html.Should().Contain("@click=\"confirmClearErrors\"");
    }

    /// <summary>
    /// The Message column truncates, so the expanded panel is the only place the full text can be
    /// read. For a startup or configuration failure that text is the entire finding — including the
    /// remediation step — and it used to survive only in a <c>title</c> tooltip, which is invisible
    /// on touch. It must be rendered as content.
    /// </summary>
    [Fact]
    public async Task AdminIndex_RendersTheUntruncatedErrorMessageInTheDetailPanel()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetIndexAsync(client);

        html.Should().Contain("class=\"error-detail-message\" x-text=\"r.message\"");
        html.Should().Contain("x-show=\"r.notRequestScoped\"");

        var css = await client.GetStringAsync("/admin/admin.css");
        css.Should().Contain(".error-detail-message");
        // Clipping the one field the panel exists to show would defeat the point.
        css.Should().NotContain(".error-detail-message { text-overflow");
    }

    /// <summary>
    /// Every local asset is served no-store but still carries a version query, because browsers
    /// that already cached an older build are the ones this has to reach.
    /// </summary>
    [Fact]
    public async Task AdminIndex_CacheBustsEveryLocalAsset()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await GetIndexAsync(client);

        var unversioned = Regex
            .Matches(html, @"(?:src|href)=""(?<url>(?!https?:|//)[^""]+\.(?:js|css))""")
            .Select(m => m.Groups["url"].Value)
            .Where(url => !Regex.IsMatch(url, @"\?v=\d+$"))
            // Vendored libraries carry their version in the filename, so a new build is a new URL.
            .Where(url => !Regex.IsMatch(url, @"-\d+\.\d+\.\d+(\.min)?\.(js|css)$"))
            .Distinct()
            .ToList();

        unversioned.Should().BeEmpty(
            "a local asset without ?v=N is served from a stale browser cache after a deploy; found: "
            + string.Join(" | ", unversioned));
    }

    [Fact]
    public async Task AdminAssets_CarrySupportingSecurityHeaders()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/index.html");

        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
    }
}
