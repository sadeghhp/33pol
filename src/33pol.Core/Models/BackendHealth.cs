namespace Pol33.Core.Models;

public sealed record BackendHealth(
    string ModelId,
    string Url,
    bool IsHealthy,
    int? StatusCode,
    string? Error,
    DateTimeOffset LastCheckedUtc);
