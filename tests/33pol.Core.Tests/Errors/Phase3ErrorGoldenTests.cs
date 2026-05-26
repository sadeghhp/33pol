using FluentAssertions;
using Pol33.Core.Errors;

namespace Pol33.Core.Tests.Errors;

public sealed class Phase3ErrorGoldenTests
{
    public static TheoryData<GatewayErrorCode> Phase3Codes { get; } = new()
    {
        GatewayErrorCode.InvalidJson,
        GatewayErrorCode.MissingModel,
        GatewayErrorCode.ModelNotAllowed,
        GatewayErrorCode.RequestTooLarge,
        GatewayErrorCode.InvalidApiKey,
        GatewayErrorCode.ExpiredApiKey,
        GatewayErrorCode.InsufficientScope,
        GatewayErrorCode.ModelNotFound,
        GatewayErrorCode.BackendUnhealthy,
        GatewayErrorCode.UpstreamError,
        GatewayErrorCode.CircuitOpen,
        GatewayErrorCode.GatewayDraining,
        GatewayErrorCode.NotReady,
    };

    [Theory]
    [MemberData(nameof(Phase3Codes))]
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
