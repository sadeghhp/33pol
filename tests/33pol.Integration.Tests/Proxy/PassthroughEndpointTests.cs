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

    [Fact]
    public async Task GetAdminConfigStatus_IsPassthrough_DoesNotInvokeUpstream()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/api/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.SendCount.Should().Be(0);
    }
}
