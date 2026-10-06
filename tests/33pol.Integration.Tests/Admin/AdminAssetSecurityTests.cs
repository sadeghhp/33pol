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
    [Fact]
    public async Task AdminIndex_ReferencesNoExternalScriptsOrStylesheets()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);

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

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);

        html.Should().NotContain("fonts.googleapis.com");
        html.Should().NotContain("fonts.gstatic.com");
        html.Should().NotContain("cdn.jsdelivr.net");
    }

    [Theory]
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

    [Fact]
    public async Task LegacyAlpineBundle_IsNotPublished()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/vendor/alpine-csp-3.14.9.min.js");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);

        var refs = Regex
            .Matches(html, @"<(?:script|link)[^>]*(?:src|href)=""(?!data:|https?://)([^""]+)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        refs.Should().NotBeEmpty();

        foreach (var reference in refs)
        {
            var path = AdminAssetTestHelpers.ResolveReferencedPath(reference);
            var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{reference} → {path} must be served locally");
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
        csp.Should().NotContain("script-src 'self' 'unsafe-inline'");
        csp.Should().NotContain("unsafe-eval");
    }

    [Fact]
    public async Task AdminIndex_IsSolidSpaShell()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);

        html.Should().Contain("id=\"root\"");
        html.Should().MatchRegex(@"<script[^>]+type=""module""[^>]+src=""/admin/assets/index-[^""]+\.js""");
        html.Should().NotContain("x-data");
        html.Should().NotContain("admin-app.js");
        html.Should().NotContain("alpine");
    }

    [Fact]
    public async Task AdminIndex_HasNoInlineScripts()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);

        Regex.Matches(html, @"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase).Count
            .Should().Be(0, "the SPA shell must not carry inline script blocks");
    }

    [Fact]
    public async Task ErrorsPageChunk_IsPublishedAndLoadable()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var errorsPage = await AdminAssetTestHelpers.GetLazyChunkAsync(client, "ErrorsPage");

        errorsPage.Length.Should().BeGreaterThan(500, "Errors page chunk must ship in the build output");
    }

    /// <summary>
    /// The Message column truncates, so the expanded panel is the only place the full text can be
    /// read. Styles for the detail message must survive the Vite bundle.
    /// </summary>
    [Fact]
    public async Task AdminIndex_RendersTheUntruncatedErrorMessageInTheDetailPanel()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await AdminAssetTestHelpers.GetMainCssAsync(client);

        css.Should().Contain(".error-detail-message");
        css.Should().NotContain(".error-detail-message { text-overflow");
    }

    /// <summary>
    /// Content-hashed bundles and versioned vendor files identify their bytes by URL; the bootstrap
    /// document and hand-maintained <c>fonts.css</c> stay uncacheable instead.
    /// </summary>
    [Fact]
    public async Task AdminIndex_CacheBustsEveryLocalAsset()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);

        var unversioned = Regex
            .Matches(html, @"(?:src|href)=""(?<url>(?!https?:|//|data:)[^""]+\.(?:js|css))""")
            .Select(m => m.Groups["url"].Value)
            .Where(url => !AdminAssetTestHelpers.IsCacheBustedLocalAsset(url))
            .Distinct()
            .ToList();

        unversioned.Should().BeEmpty(
            "a local asset without a content hash or vendor version in the filename is stale after deploy; found: "
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
