namespace Pol33.Core.Models;

public sealed class BackendAdminDto
{
    public required string ModelId { get; init; }

    public required string Url { get; init; }

    public bool IsHealthy { get; init; }

    public string? Alias { get; init; }
}
