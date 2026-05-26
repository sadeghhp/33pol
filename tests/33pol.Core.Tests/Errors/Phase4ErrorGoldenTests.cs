using FluentAssertions;
using Pol33.Core.Errors;

namespace Pol33.Core.Tests.Errors;

public sealed class Phase4ErrorGoldenTests
{
    public static TheoryData<GatewayErrorCode> Phase4Codes { get; } = new()
    {
        GatewayErrorCode.RateLimitExceeded,
        GatewayErrorCode.QuotaExceeded,
        GatewayErrorCode.ConcurrencyLimitExceeded,
    };

    [Theory]
    [MemberData(nameof(Phase4Codes))]
    public void Write_DefaultMessage_MatchesGoldenFile(GatewayErrorCode code)
    {
        var writer = new OpenAiErrorResponseWriter();
        var actual = writer.Write(code).Json.Trim();

        var goldenPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "TestData",
            $"error-{code.ToCodeString()}.golden.json");

        var expected = File.ReadAllText(Path.GetFullPath(goldenPath)).Trim();
        actual.Should().Be(expected);
    }
}
