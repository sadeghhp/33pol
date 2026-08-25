namespace Pol33.Core.Models;

/// <param name="LastTransitionUtc">When <paramref name="IsHealthy"/> last flipped; null until it has.</param>
public sealed record BackendHealth(
    string ModelId,
    string Url,
    bool IsHealthy,
    int? StatusCode,
    string? Error,
    DateTimeOffset LastCheckedUtc,
    DateTimeOffset? LastTransitionUtc = null);
