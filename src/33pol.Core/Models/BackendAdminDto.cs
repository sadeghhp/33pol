namespace Pol33.Core.Models;

public sealed class BackendAdminDto
{
    public required string ModelId { get; init; }

    public required string Url { get; init; }

    public bool IsHealthy { get; init; }

    /// <summary>
    /// Whether an operator has this route in service (<see cref="ModelRouteStates"/>). A stopped
    /// route is not probed, so this is what tells an operator why its health looks frozen.
    /// </summary>
    public string State { get; init; } = ModelRouteStates.Serving;

    public string? Alias { get; init; }

    /// <summary>HTTP status of the last probe, when it got a response.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Why the last probe failed (or a note such as "auth required"); null when clean.</summary>
    public string? Error { get; init; }

    public DateTimeOffset? LastCheckedUtc { get; init; }

    /// <summary>When the healthy/unhealthy state last flipped.</summary>
    public DateTimeOffset? LastTransitionUtc { get; init; }
}
