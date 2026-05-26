using System.Text.Json;
using FluentAssertions;
using Pol33.Core.Errors;

namespace Pol33.Core.Tests.Errors;

public sealed class ErrorResultTests
{
    [Fact]
    public void Serialize_UsesOpenAiEnvelopeShape()
    {
        var result = ErrorResult.FromCode(
            GatewayErrorCode.InvalidApiKey,
            "Invalid API key",
            "authentication_error",
            param: "authorization");

        var json = JsonSerializer.Serialize(result);

        json.Should().Contain("\"error\":");
        json.Should().Contain("\"message\":\"Invalid API key\"");
        json.Should().Contain("\"type\":\"authentication_error\"");
        json.Should().Contain("\"code\":\"invalid_api_key\"");
        json.Should().Contain("\"param\":\"authorization\"");
    }
}
