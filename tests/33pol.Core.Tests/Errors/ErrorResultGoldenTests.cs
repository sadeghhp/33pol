using System.Text.Json;
using System.Text.Json.Serialization;
using Pol33.Core.Errors;

namespace Pol33.Core.Tests.Errors;

public sealed class ErrorResultGoldenTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Serialize_MatchesGoldenFile()
    {
        var result = ErrorResult.FromCode(
            GatewayErrorCode.InvalidApiKey,
            "Invalid API key",
            "authentication_error",
            param: "authorization");

        var actual = JsonSerializer.Serialize(result, SerializerOptions);
        var expectedPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "TestData",
            "error-invalid-api-key.golden.json");

        var expected = File.ReadAllText(Path.GetFullPath(expectedPath))
            .Trim();

        actual.Should().Be(expected);
    }
}
