using Pol33.Core.Configuration;

namespace Pol33.Core.Tests.Configuration;

public sealed class GatewayCorsOptionsTests
{
    [Fact]
    public void NormalizeOrigins_TrimsAndStripsTrailingSlash()
    {
        var result = GatewayCorsOptions.NormalizeOrigins(
            ["  https://app.example.com/  ", "http://localhost:5173/", ""]);

        result.Should().Equal(["https://app.example.com", "http://localhost:5173"]);
    }

    [Fact]
    public void GetNormalizedOrigins_UsesAllowedOriginsProperty()
    {
        var options = new GatewayCorsOptions
        {
            AllowedOrigins = ["http://localhost:3000/"],
        };

        options.GetNormalizedOrigins().Should().Equal(["http://localhost:3000"]);
    }
}
