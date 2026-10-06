using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Key lifecycle affordances in the Solid admin bundle.
/// </summary>
public sealed class AdminConsoleKeyLifecycleActionsTests
{
    [Fact]
    public async Task StatusFilter_OffersArchivedKeys()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var keysPage = await AdminAssetTestHelpers.GetLazyChunkAsync(client, "KeysPage");

        keysPage.Should().Contain("archived");
        keysPage.Should().Contain("revoked");
        keysPage.Should().Contain("panel-keys");
        keysPage.Should().Contain("Revoke");
        keysPage.Should().Contain("/admin/api/keys/");
        keysPage.Should().Contain("/revoke");
    }

    [Fact]
    public async Task LifecycleConflicts_SurfaceTheServersOwnMessage()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var js = await AdminAssetTestHelpers.GetBundledAppJsAsync(client);

        js.Should().Contain("key_has_usage");
        js.Should().Contain("409");
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
