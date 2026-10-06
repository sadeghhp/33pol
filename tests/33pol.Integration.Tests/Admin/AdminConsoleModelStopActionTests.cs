using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Model stop/start controls in the Solid routing bundle.
/// </summary>
public sealed class AdminConsoleModelStopActionTests
{
    [Fact]
    public async Task RoutingBundle_ReferencesModelStateEndpoints()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var routing = await AdminAssetTestHelpers.GetLazyChunkAsync(client, "RoutingPage");

        routing.Should().Contain("/admin/api/models");
    }
}
