using System.Net;
using System.Net.Http.Headers;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Delivery policy for the admin console: what may be cached, what must not be, and what may be
/// compressed.
/// </summary>
public sealed class AdminAssetCachingTests
{
    private const string ImmutableFont = "/admin/vendor/fonts/IBMPlexSans-400.woff2";
    private const string AdminKey = "sk-33pol-integration-admin-key";

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string path, string? acceptEncoding = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null)
        {
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(acceptEncoding));
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task AdminIndex_IsNeverCached()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await GetAsync(client, "/admin/index.html");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cacheControl = response.Headers.CacheControl!.ToString();
        cacheControl.Should().Contain("no-store");
        cacheControl.Should().NotContain("immutable");
    }

    [Fact]
    public async Task ContentHashedAssets_AreImmutablyCacheable()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);
        var jsPath = AdminAssetTestHelpers.ExtractMainBundlePath(html);
        var cssPath = AdminAssetTestHelpers.ExtractMainCssPath(html);

        foreach (var path in new[] { jsPath, cssPath })
        {
            var response = await GetAsync(client, path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var cacheControl = response.Headers.CacheControl!;
            cacheControl.Public.Should().BeTrue();
            cacheControl.MaxAge.Should().Be(TimeSpan.FromDays(365));
            cacheControl.ToString().Should().Contain("immutable");
            cacheControl.NoStore.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("/admin/vendor/fonts/IBMPlexSans-400.woff2")]
    [InlineData("/admin/vendor/fonts/IBMPlexMono-400.woff2")]
    public async Task VendoredAssets_AreImmutablyCacheable(string path)
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await GetAsync(client, path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cacheControl = response.Headers.CacheControl!;
        cacheControl.Public.Should().BeTrue();
        cacheControl.MaxAge.Should().Be(TimeSpan.FromDays(365));
        cacheControl.ToString().Should().Contain("immutable");
        cacheControl.NoStore.Should().BeFalse();
        response.Headers.Pragma.Should().BeEmpty();
    }

    [Fact]
    public async Task HandVersionedVendorCss_IsNotImmutablyCached()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await GetAsync(client, "/admin/vendor/fonts.css");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cacheControl = response.Headers.CacheControl!.ToString();
        cacheControl.Should().Contain(
            "no-store", "fonts.css is source and must not be cached immutably");
        cacheControl.Should().NotContain("immutable");
    }

    [Fact]
    public async Task AdminTextAssets_AreCompressed()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);
        var paths = new[]
        {
            AdminAssetTestHelpers.ExtractMainBundlePath(html),
            AdminAssetTestHelpers.ExtractMainCssPath(html),
            "/admin/index.html",
        };

        foreach (var path in paths)
        {
            var response = await GetAsync(client, path, "br");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentEncoding.Should().Contain(
                "br", $"{path} is text and should not travel uncompressed");
        }
    }

    [Fact]
    public async Task Fonts_AreNotRecompressed()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await GetAsync(client, ImmutableFont, "br");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().BeEmpty();
    }

    [Fact]
    public async Task LiveStream_IsNotCompressed()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api/live?limit=1");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Content.Headers.ContentEncoding.Should().BeEmpty(
            "compressing text/event-stream buffers frames and stalls the live console");

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var buffer = new char[256];
        var read = await reader.ReadAsync(buffer, cts.Token);

        read.Should().BeGreaterThan(0, "the live stream must deliver bytes, not buffer them");
        new string(buffer, 0, read).Should().StartWith(
            "event: update", "the first frame is the current summary");
    }

    [Fact]
    public async Task NonAdminResponses_AreNotCompressed()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await GetAsync(client, "/", "br");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentEncoding.Should().BeEmpty(
            "response compression is an /admin asset-delivery measure and must not reach the gateway's "
            + "own data path");
    }

    [Fact]
    public async Task AdminAssets_StillCarrySecurityHeaders()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);
        var paths = new[]
        {
            "/admin/index.html",
            AdminAssetTestHelpers.ExtractMainBundlePath(html),
            ImmutableFont,
        };

        foreach (var path in paths)
        {
            var response = await GetAsync(client, path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.GetValues("Content-Security-Policy").Should()
                .Contain(AdminSecurityHeaders.ContentSecurityPolicy);
            response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
            response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
            response.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
        }
    }
}
