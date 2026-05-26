namespace Pol33.Core.Errors;

public static class GatewayErrorCodeExtensions
{
    public static string ToCodeString(this GatewayErrorCode code) => code switch
    {
        GatewayErrorCode.InvalidJson => "invalid_json",
        GatewayErrorCode.MissingModel => "missing_model",
        GatewayErrorCode.ModelNotAllowed => "model_not_allowed",
        GatewayErrorCode.RequestTooLarge => "request_too_large",
        GatewayErrorCode.InvalidApiKey => "invalid_api_key",
        GatewayErrorCode.ExpiredApiKey => "expired_api_key",
        GatewayErrorCode.InsufficientScope => "insufficient_scope",
        GatewayErrorCode.ModelNotFound => "model_not_found",
        GatewayErrorCode.RateLimitExceeded => "rate_limit_exceeded",
        GatewayErrorCode.QuotaExceeded => "quota_exceeded",
        GatewayErrorCode.ConcurrencyLimitExceeded => "concurrency_limit_exceeded",
        GatewayErrorCode.BackendUnhealthy => "backend_unhealthy",
        GatewayErrorCode.UpstreamError => "upstream_error",
        GatewayErrorCode.CircuitOpen => "circuit_open",
        GatewayErrorCode.GatewayDraining => "gateway_draining",
        GatewayErrorCode.NotReady => "not_ready",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown gateway error code."),
    };
}
