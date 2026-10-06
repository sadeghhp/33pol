using System.Net;
using System.Text.RegularExpressions;
using Pol33.App;

namespace Pol33.Integration.Tests.Admin;

internal static class AdminAssetTestHelpers
{
    private static readonly Regex MainBundlePath = new(
        @"<script[^>]+type=""module""[^>]+src=""(?<path>/admin/assets/index-[^""]+\.js)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MainCssPath = new(
        @"<link[^>]+rel=""stylesheet""[^>]+href=""(?<path>/admin/assets/index-[^""]+\.css)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RatelimitsChunkPath = new(
        @"<link[^>]+rel=""modulepreload""[^>]+href=""(?<path>/admin/assets/ratelimits-[^""]+\.js)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex VendorVersionPattern = new(
        @"-\d+\.\d+\.\d+(\.min)?\.(js|css)$",
        RegexOptions.CultureInvariant);

    public static async Task<string> GetIndexHtmlAsync(HttpClient client)
    {
        var response = await client.GetAsync("/admin/index.html");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    public static string ExtractMainBundlePath(string html) =>
        ExtractPath(html, MainBundlePath, "main JS bundle");

    public static string ExtractMainCssPath(string html) =>
        ExtractPath(html, MainCssPath, "main CSS bundle");

    public static string ExtractRatelimitsChunkPath(string html) =>
        ExtractPath(html, RatelimitsChunkPath, "ratelimits chunk");

    public static async Task<string> GetMainBundleAsync(HttpClient client)
    {
        var html = await GetIndexHtmlAsync(client);
        return await GetAssetTextAsync(client, ExtractMainBundlePath(html));
    }

    public static async Task<string> GetMainCssAsync(HttpClient client)
    {
        var html = await GetIndexHtmlAsync(client);
        return await GetAssetTextAsync(client, ExtractMainCssPath(html));
    }

    public static async Task<string> GetRatelimitsChunkAsync(HttpClient client)
    {
        var html = await GetIndexHtmlAsync(client);
        return await GetAssetTextAsync(client, ExtractRatelimitsChunkPath(html));
    }

    /// <summary>
    /// Main entry plus the eagerly preloaded ratelimits chunk — the closest analogue to the old
    /// monolithic <c>admin-app.js</c>.
    /// </summary>
    public static async Task<string> GetBundledAppJsAsync(HttpClient client)
    {
        var html = await GetIndexHtmlAsync(client);
        var main = await GetAssetTextAsync(client, ExtractMainBundlePath(html));
        var ratelimits = await GetAssetTextAsync(client, ExtractRatelimitsChunkPath(html));
        return main + "\n" + ratelimits;
    }

    public static async Task<string> GetLazyChunkAsync(HttpClient client, string chunkPrefix)
    {
        var main = await GetMainBundleAsync(client);
        var relative = ExtractLazyChunkRelativePath(main, chunkPrefix);
        return await GetAssetTextAsync(client, "/admin/" + relative.TrimStart('/'));
    }

    public static async Task<string> GetAssetTextAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Resolve a reference from <c>index.html</c> to an absolute request path.
    /// Vite may emit <c>/vendor/…</c> for assets that live under <c>/admin/vendor/</c>.
    /// </summary>
    public static string ResolveReferencedPath(string reference)
    {
        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return reference;
        }

        if (reference.StartsWith('/'))
        {
            if (reference.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase))
            {
                return reference;
            }

            if (reference.StartsWith("/vendor/", StringComparison.OrdinalIgnoreCase))
            {
                return "/admin" + reference;
            }

            return reference;
        }

        return "/admin/" + reference.TrimStart('/');
    }

    public static bool HasContentHashInFileName(string url)
    {
        var fileName = Path.GetFileName(url.Split('?', 2)[0]);
        return AdminAssetFileNames.HasContentHash(fileName);
    }

    public static bool HasVendorVersionInFileName(string url) =>
        VendorVersionPattern.IsMatch(Path.GetFileName(url.Split('?', 2)[0]));

    public static bool IsCacheBustedLocalAsset(string url)
    {
        var path = url.Split('?', 2)[0];
        var fileName = Path.GetFileName(path);

        if (fileName.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("fonts.css", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/vendor/fonts/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasContentHashInFileName(path) || HasVendorVersionInFileName(path);
    }

    private static string ExtractPath(string html, Regex pattern, string label)
    {
        var match = pattern.Match(html);
        match.Success.Should().BeTrue($"index.html must reference a {label}");
        return match.Groups["path"].Value;
    }

    private static string ExtractLazyChunkRelativePath(string mainBundle, string chunkPrefix)
    {
        var match = Regex.Match(
            mainBundle,
            $@"import\(""(\./{Regex.Escape(chunkPrefix)}-[^""]+\.js)""\)",
            RegexOptions.CultureInvariant);
        match.Success.Should().BeTrue($"main bundle must lazy-load {chunkPrefix} chunk");
        return "assets/" + match.Groups[1].Value.TrimStart('.').TrimStart('/');
    }
}
