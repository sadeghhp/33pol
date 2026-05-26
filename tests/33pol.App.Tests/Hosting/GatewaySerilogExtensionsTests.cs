using Pol33.App.Hosting;

namespace Pol33.App.Tests.Hosting;

public sealed class GatewaySerilogExtensionsTests
{
    [Fact]
    public void RequestLogMessageTemplate_DoesNotIncludeRequestBodyTokens()
    {
        GatewaySerilogExtensions.RequestLogMessageTemplate.Should().NotContain("Body");
        GatewaySerilogExtensions.RequestLogMessageTemplate.Should().Contain("RequestMethod");
        GatewaySerilogExtensions.RequestLogMessageTemplate.Should().Contain("RequestPath");
        GatewaySerilogExtensions.RequestLogMessageTemplate.Should().Contain("StatusCode");
        GatewaySerilogExtensions.RequestLogMessageTemplate.Should().Contain("Elapsed");
    }
}
