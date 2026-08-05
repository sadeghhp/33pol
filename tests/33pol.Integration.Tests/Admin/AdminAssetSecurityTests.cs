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
    [InlineData("/admin/vendor/alpine-3.14.9.min.js")]
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
