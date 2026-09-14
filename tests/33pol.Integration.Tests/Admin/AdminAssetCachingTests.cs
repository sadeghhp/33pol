using System.Net;
using System.Net.Http.Headers;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Delivery policy for the admin console: what may be cached, what must not be, and what may be
/// compressed.
/// </summary>
/// <remarks>
/// The console used to serve every asset <c>no-store</c> and uncompressed, so every visit re-fetched
/// roughly 925 KB — 273 KB of it fonts that have never changed — without a single cache hit. These
/// tests pin the two halves of the fix and, more importantly, the edges it must not cross: the
/// bootstrap document must stay uncacheable, the live stream must stay uncompressed, and the
/// security headers must survive on every branch.
/// </remarks>
public sealed class AdminAssetCachingTests
{
    private const string ImmutableAsset = "/admin/vendor/alpine-csp-3.14.9.min.js";
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

    /// <summary>
    /// The bootstrap document names every other asset, so caching it is what strands an operator on
    /// a console that no longer matches the gateway.
    /// </summary>
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

    /// <summary>
    /// The hand-versioned <c>?v=N</c> assets stay uncacheable until content hashing arrives: a query
    /// string is not part of the cache identity for every intermediary, so caching them for a year
    /// would be caching the wrong thing.
    /// </summary>
    [Theory]
    [InlineData("/admin/admin-app.js?v=37")]
    [InlineData("/admin/admin.css?v=24")]
    public async Task QueryVersionedAssets_AreNotYetImmutablyCached(string path)
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await GetAsync(client, path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Contain("no-store");
    }

    /// <summary>
    /// Vendored assets carry their version in the filename, or are immutable by nature, so a new
    /// build is always a new URL and a year is safe.
    /// </summary>
    [Theory]
    [InlineData(ImmutableAsset)]
    [InlineData(ImmutableFont)]
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
        // Pragma would contradict Cache-Control for an HTTP/1.0 intermediary.
        response.Headers.Pragma.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/admin/admin.css?v=24")]
    [InlineData("/admin/admin-app.js?v=37")]
    [InlineData("/admin/index.html")]
    public async Task AdminTextAssets_AreCompressed(string path)
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await GetAsync(client, path, "br");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().Contain(
            "br", $"{path} is text and should not travel uncompressed");
    }

    /// <summary>
    /// woff2 is already compressed; a second pass spends CPU and returns nothing.
    /// </summary>
    [Fact]
    public async Task Fonts_AreNotRecompressed()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await GetAsync(client, ImmutableFont, "br");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().BeEmpty();
    }

    /// <summary>
    /// The guard on the one way enabling compression could break production.
    /// </summary>
    /// <remarks>
    /// The admin Overview is pushed over SSE. A compressor sitting on that response holds frames in
    /// its buffer instead of flushing them, so the stream would stall — and the console would either
    /// go silent or quietly fall back to 2s polling, which looks like "the dashboard is a bit slow"
    /// rather than a broken deploy. The response-compression middleware wraps endpoint responses as
    /// well as static files, so this is not hypothetical.
    /// </remarks>
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

        // Headers alone would not catch a compressor that simply never flushes, so read far enough
        // to prove a frame actually arrives.
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var buffer = new char[256];
        var read = await reader.ReadAsync(buffer, cts.Token);

        read.Should().BeGreaterThan(0, "the live stream must deliver bytes, not buffer them");
        new string(buffer, 0, read).Should().StartWith(
            "event: update", "the first frame is the current summary");
    }

    /// <summary>
    /// Compression is scoped to <c>/admin</c>, and the scope is the point.
    /// </summary>
    /// <remarks>
    /// The compressor has to sit ahead of the static-file handler to reach the console's assets, and
    /// WebApplication runs the terminal endpoint middleware after everything registered there — so
    /// registering it unscoped silently puts a compressor on the inference data path too. That is
    /// CPU spent per response on a proxy whose overhead is a measured design constraint, for nothing:
    /// an upstream that already compressed is passed through untouched, and a streamed token chunk
    /// is far too small to compress. This test is the boundary; <c>/</c> is an unauthenticated JSON
    /// endpoint outside <c>/admin</c> that the unscoped registration did compress.
    /// </remarks>
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

    /// <summary>
    /// The cache policy branches; the security headers must not. A surface that keeps its CSP on one
    /// kind of asset and loses it on another has lost it.
    /// </summary>
    [Theory]
    [InlineData("/admin/index.html")]
    [InlineData("/admin/admin-app.js?v=37")]
    [InlineData(ImmutableAsset)]
    [InlineData(ImmutableFont)]
    public async Task AdminAssets_StillCarrySecurityHeaders(string path)
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await GetAsync(client, path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Content-Security-Policy").Should()
            .Contain(AdminSecurityHeaders.ContentSecurityPolicy);
        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Contain("no-referrer");
    }
}
