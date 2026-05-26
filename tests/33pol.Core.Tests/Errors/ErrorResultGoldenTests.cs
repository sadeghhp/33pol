using Pol33.Core.Errors;

namespace Pol33.Core.Tests.Errors;

public sealed class ErrorResultGoldenTests
{
    [Fact]
    public void Write_InvalidApiKey_MatchesGoldenFile()
    {
        var writer = new OpenAiErrorResponseWriter();
        var actual = writer.Write(GatewayErrorCode.InvalidApiKey).Json.Trim();
        var expectedPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "TestData",
            "error-invalid_api_key.golden.json");

        var expected = File.ReadAllText(Path.GetFullPath(expectedPath)).Trim();
        actual.Should().Be(expected);
    }
}
