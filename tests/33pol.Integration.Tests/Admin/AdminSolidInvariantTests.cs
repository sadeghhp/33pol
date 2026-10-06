using System.Net;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Invariants of the SolidJS + Vite admin shell (M6).
/// </summary>
public sealed class AdminSolidInvariantTests
{
    [Fact]
    public async Task AdminIndex_HasNoAlpineDirectives()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);

        html.Should().NotContain("x-data");
        html.Should().NotContain("x-show");
        html.Should().NotContain("x-text");
        html.Should().NotContain("x-for");
        html.Should().NotContain("x-model");
        html.Should().NotContain("@click");
        html.Should().NotContain("alpine");
    }

    [Fact]
    public async Task ContentHashedJs_IsServedWithImmutableCache()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var html = await AdminAssetTestHelpers.GetIndexHtmlAsync(client);
        var path = AdminAssetTestHelpers.ExtractMainBundlePath(html);

        AdminAssetTestHelpers.HasContentHashInFileName(path).Should().BeTrue();

        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cacheControl = response.Headers.CacheControl!;
        cacheControl.Public.Should().BeTrue();
        cacheControl.MaxAge.Should().Be(TimeSpan.FromDays(365));
        cacheControl.ToString().Should().Contain("immutable");
        cacheControl.NoStore.Should().BeFalse();
    }
}
