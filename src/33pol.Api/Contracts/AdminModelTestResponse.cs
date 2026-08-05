using Microsoft.AspNetCore.Http;

namespace Pol33.Api.Contracts;

public sealed class AdminModelTestResponse
{
    public bool Ok { get; set; }

    public string ModelId { get; set; } = string.Empty;

    /// <summary>Canonical model type the probe dispatched on (see <c>ModelTypes</c>).</summary>
    public string? ModelType { get; set; }

    /// <summary>Upstream path the probe called, e.g. <c>/v1/embeddings</c>. Null when no call was made.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// False when the model's type has no health check the gateway can express. The UI reports this
    /// as "not available" rather than a failure, since nothing was actually tested.
    /// </summary>
    public bool Supported { get; set; } = true;

    public long LatencyMs { get; set; }

    public int? StatusCode { get; set; }

    public string? Detail { get; set; }

    public string? Content { get; set; }

    /// <summary>
    /// The next step an operator can take, when the gateway can narrow the failure down to one.
    /// Null when it cannot — a wrong hint costs more than no hint.
    /// </summary>
    public string? Hint { get; set; }

    public int SuggestedStatusCode { get; set; } = StatusCodes.Status200OK;
}
