namespace Pol33.Core.Identity;

public sealed class TenantContext
{
    public required string TenantId { get; init; }

    public required string ApiKeyId { get; init; }

    /// <summary>
    /// The label the key was issued with, for the metrics that name a caller. Never the secret or
    /// its prefix; null when the key has no label.
    /// </summary>
    public string? ApiKeyLabel { get; init; }

    public string? TenantSlug { get; init; }

    public string? PlanSlug { get; init; }

    public string? CostCenter { get; init; }

    public ApiKeyRole Role { get; init; }

    public IReadOnlyList<string> GrantedModels { get; init; } = Array.Empty<string>();
}
