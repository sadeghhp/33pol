using FluentAssertions;
using Pol33.Core.Errors;

namespace Pol33.Core.Tests.Errors;

public sealed class GatewayErrorCodeTests
{
    public static TheoryData<GatewayErrorCode, string> AllCodes { get; } = new()
    {
        { GatewayErrorCode.InvalidJson, "invalid_json" },
        { GatewayErrorCode.RequestIncomplete, "request_incomplete" },
        { GatewayErrorCode.MissingModel, "missing_model" },
        { GatewayErrorCode.ModelNotAllowed, "model_not_allowed" },
        { GatewayErrorCode.RequestTooLarge, "request_too_large" },
        { GatewayErrorCode.InvalidApiKey, "invalid_api_key" },
        { GatewayErrorCode.ExpiredApiKey, "expired_api_key" },
        { GatewayErrorCode.InsufficientScope, "insufficient_scope" },
        { GatewayErrorCode.ModelNotFound, "model_not_found" },
        { GatewayErrorCode.RateLimitExceeded, "rate_limit_exceeded" },
        { GatewayErrorCode.QuotaExceeded, "quota_exceeded" },
        { GatewayErrorCode.ConcurrencyLimitExceeded, "concurrency_limit_exceeded" },
        { GatewayErrorCode.BackendUnhealthy, "backend_unhealthy" },
        { GatewayErrorCode.UpstreamError, "upstream_error" },
        { GatewayErrorCode.CircuitOpen, "circuit_open" },
        { GatewayErrorCode.GatewayDraining, "gateway_draining" },
        { GatewayErrorCode.NotReady, "not_ready" },
    };

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void ToCodeString_ReturnsStableSnakeCase(GatewayErrorCode code, string expected)
    {
        code.ToCodeString().Should().Be(expected);
    }

    [Fact]
    public void EnumValues_MatchCatalogRowCount()
    {
        Enum.GetValues<GatewayErrorCode>().Should().HaveCount(AllCodes.Count);
    }
}
