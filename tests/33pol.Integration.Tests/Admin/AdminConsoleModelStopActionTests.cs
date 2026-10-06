using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Routing bundle delivery invariants (stop/start behaviour covered by Playwright E2E).
/// </summary>
public sealed class AdminConsoleModelStopActionTests
{
    [Fact]
    public async Task RoutingPageChunk_IsPublishedAndLoadable()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var chunk = await AdminAssetTestHelpers.GetLazyChunkAsync(client, "RoutingPage");

        chunk.Length.Should().BeGreaterThan(500, "Routing page chunk must ship in the build output");
    }
}
