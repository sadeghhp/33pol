using System.Net;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Proxy;

[Trait("Category", "V1Parity")]
public sealed class PassthroughEndpointTests
{
    [Fact]
    public async Task GetHealth_IsPassthrough_DoesNotInvokeUpstream()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.SendCount.Should().Be(0);
    }

    /// <summary>
    /// Authenticated, because the control plane is closed to anonymous callers in every
    /// configuration — including a gateway with no database, which is what this used to rely on.
    /// </summary>
    [Fact]
    public async Task GetAdminConfigStatus_IsPassthrough_DoesNotInvokeUpstream()
    {
        var handler = new MockUpstreamHandler();
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            upstreamHandler: handler);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync("/admin/api/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.SendCount.Should().Be(0);
    }
}
