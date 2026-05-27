namespace Pol33.Core.Models;

public sealed class ModelGrantsResponse
{
    public required IReadOnlyList<string> ModelIds { get; init; }

    /// <summary>When true, no explicit allowlist rows exist (tenant-open or key inherits tenant).</summary>
    public bool UsesDefaultAccess { get; init; }
}

public sealed class ReplaceModelGrantsRequest
{
    public IReadOnlyList<string> ModelIds { get; init; } = [];
}
