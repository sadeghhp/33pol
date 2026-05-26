using System.Text.Json.Serialization;

namespace Pol33.Core.Errors;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GatewayErrorCode
{
    InvalidJson,
    MissingModel,
    ModelNotAllowed,
    RequestTooLarge,
    InvalidApiKey,
    ExpiredApiKey,
    InsufficientScope,
    ModelNotFound,
    RateLimitExceeded,
    QuotaExceeded,
    ConcurrencyLimitExceeded,
    BackendUnhealthy,
    UpstreamError,
    CircuitOpen,
    GatewayDraining,
    NotReady,
}
