namespace Pol33.Core.Identity;

public sealed class TenantContext
{
    public required string TenantId { get; init; }

    public required string ApiKeyId { get; init; }

    public string? TenantSlug { get; init; }

    public string? PlanSlug { get; init; }

    public string? CostCenter { get; init; }

    public ApiKeyRole Role { get; init; }

    public IReadOnlyList<string> GrantedModels { get; init; } = Array.Empty<string>();
}
