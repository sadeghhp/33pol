using FluentAssertions;
using Pol33.Core.Errors;

namespace Pol33.Core.Tests.Errors;

public sealed class GatewayErrorCatalogTests
{
    public static TheoryData<GatewayErrorCode, int, string, int> Phase3Definitions { get; } = new()
    {
        { GatewayErrorCode.InvalidJson, 400, "invalid_request_error", 3 },
        { GatewayErrorCode.RequestIncomplete, 400, "invalid_request_error", 3 },
        { GatewayErrorCode.MissingModel, 400, "invalid_request_error", 3 },
        { GatewayErrorCode.ModelNotAllowed, 400, "invalid_request_error", 3 },
        { GatewayErrorCode.RequestTooLarge, 400, "invalid_request_error", 3 },
        { GatewayErrorCode.InvalidApiKey, 401, "authentication_error", 3 },
        { GatewayErrorCode.ExpiredApiKey, 401, "authentication_error", 3 },
        { GatewayErrorCode.InsufficientScope, 403, "permission_error", 3 },
        { GatewayErrorCode.ModelNotFound, 404, "invalid_request_error", 3 },
        { GatewayErrorCode.BackendUnhealthy, 502, "backend_error", 3 },
        { GatewayErrorCode.UpstreamError, 502, "backend_error", 3 },
        { GatewayErrorCode.CircuitOpen, 502, "backend_error", 3 },
        { GatewayErrorCode.GatewayDraining, 503, "service_unavailable", 3 },
        { GatewayErrorCode.NotReady, 503, "service_unavailable", 3 },
    };

    [Theory]
    [MemberData(nameof(Phase3Definitions))]
    public void Get_Phase3Code_MatchesCatalog(
        GatewayErrorCode code,
        int expectedStatus,
        string expectedType,
        int expectedPhase)
    {
        var definition = GatewayErrorCatalog.Get(code);

        definition.HttpStatusCode.Should().Be(expectedStatus);
        definition.Type.Should().Be(expectedType);
        definition.Phase.Should().Be(expectedPhase);
        definition.DefaultMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void IsPhase3_ReturnsTrueForAllPhase3Codes()
    {
        foreach (var row in Phase3Definitions)
        {
            var code = (GatewayErrorCode)row[0];
            GatewayErrorCatalog.IsPhase3(code).Should().BeTrue();
        }
    }

    [Fact]
    public void IsPhase3_ReturnsFalseForPhase4Codes()
    {
        GatewayErrorCatalog.IsPhase3(GatewayErrorCode.RateLimitExceeded).Should().BeFalse();
    }
}
