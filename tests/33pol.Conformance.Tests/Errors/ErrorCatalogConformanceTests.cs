using Pol33.Core.Errors;

namespace Pol33.Conformance.Tests.Errors;

/// <summary>
/// GA conformance: every documented SDK error code produces stable OpenAI-compatible JSON.
/// </summary>
public sealed class ErrorCatalogConformanceTests
{
    public static IEnumerable<object[]> AllErrorCodes() =>
        Enum.GetValues<GatewayErrorCode>().Select(code => new object[] { code });

    [Theory]
    [MemberData(nameof(AllErrorCodes))]
    public void Write_DefaultMessage_MatchesGoldenEnvelope(GatewayErrorCode code)
    {
        var writer = new OpenAiErrorResponseWriter();
        var actual = writer.Write(code).Json.Trim();

        var goldenPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            $"error-{code.ToCodeString()}.golden.json");

        File.Exists(goldenPath).Should().BeTrue($"missing golden file for {code}");
        var expected = File.ReadAllText(goldenPath).Trim();

        actual.Should().Be(expected);
    }
}
