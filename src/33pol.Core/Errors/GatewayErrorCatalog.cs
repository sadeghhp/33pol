namespace Pol33.Core.Errors;

public static class GatewayErrorCatalog
{
    public static GatewayErrorDefinition Get(GatewayErrorCode code) =>
        Definitions.TryGetValue(code, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown gateway error code.");

    public static bool IsPhase3(GatewayErrorCode code) => Get(code).Phase == 3;

    private static readonly IReadOnlyDictionary<GatewayErrorCode, GatewayErrorDefinition> Definitions =
        new Dictionary<GatewayErrorCode, GatewayErrorDefinition>
        {
            [GatewayErrorCode.InvalidJson] = new(
                400,
                "invalid_request_error",
                "Invalid JSON in request body.",
                Phase: 3),
            [GatewayErrorCode.MissingModel] = new(
                400,
                "invalid_request_error",
                "Missing required field: model.",
                Phase: 3,
                Param: "model"),
            [GatewayErrorCode.ModelNotAllowed] = new(
                400,
                "invalid_request_error",
                "This model is not allowed for your plan.",
                Phase: 3,
                Param: "model"),
            [GatewayErrorCode.RequestTooLarge] = new(
                400,
                "invalid_request_error",
                "Request body is too large.",
                Phase: 3),
            [GatewayErrorCode.InvalidApiKey] = new(
                401,
                "authentication_error",
                "Invalid or missing API key.",
                Phase: 3,
                Param: "authorization"),
            [GatewayErrorCode.ExpiredApiKey] = new(
                401,
                "authentication_error",
                "The API key has expired.",
                Phase: 3,
                Param: "authorization"),
            [GatewayErrorCode.InsufficientScope] = new(
                403,
                "permission_error",
                "The API key does not have permission for this operation.",
                Phase: 3),
            [GatewayErrorCode.ModelNotFound] = new(
                404,
                "invalid_request_error",
                "The requested model was not found.",
                Phase: 3,
                Param: "model"),
            [GatewayErrorCode.RateLimitExceeded] = new(
                429,
                "rate_limit_error",
                "Rate limit exceeded.",
                Phase: 4),
            [GatewayErrorCode.QuotaExceeded] = new(
                429,
                "rate_limit_error",
                "Quota exceeded.",
                Phase: 4),
            [GatewayErrorCode.ConcurrencyLimitExceeded] = new(
                429,
                "rate_limit_error",
                "Concurrency limit exceeded.",
                Phase: 4),
            [GatewayErrorCode.BackendUnhealthy] = new(
                502,
                "backend_error",
                "The model backend is unhealthy.",
                Phase: 3),
            [GatewayErrorCode.UpstreamError] = new(
                502,
                "backend_error",
                "Failed to forward request to backend.",
                Phase: 3),
            [GatewayErrorCode.CircuitOpen] = new(
                502,
                "backend_error",
                "The circuit breaker is open for this backend.",
                Phase: 3),
            [GatewayErrorCode.GatewayDraining] = new(
                503,
                "service_unavailable",
                "The gateway is draining and not accepting new requests.",
                Phase: 3),
            [GatewayErrorCode.NotReady] = new(
                503,
                "service_unavailable",
                "The gateway is not ready.",
                Phase: 3),
        };
}

public sealed record GatewayErrorDefinition(
    int HttpStatusCode,
    string Type,
    string DefaultMessage,
    int Phase,
    string? Param = null);
