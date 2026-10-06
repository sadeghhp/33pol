using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Key lifecycle styling invariants in the Solid admin bundle (behaviour covered by Playwright E2E).
/// </summary>
public sealed class AdminConsoleKeyLifecycleActionsTests
{
    [Fact]
    public async Task KeysPageChunk_IsPublishedAndLoadable()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var chunk = await AdminAssetTestHelpers.GetLazyChunkAsync(client, "KeysPage");

        chunk.Length.Should().BeGreaterThan(500, "Keys page chunk must ship in the build output");
    }

    [Fact]
    public async Task ArchivedStatusChip_HasItsOwnStyle()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var css = await AdminAssetTestHelpers.GetMainCssAsync(client);

        css.Should().Contain(".status-chip.muted");
    }
}
