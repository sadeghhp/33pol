namespace Pol33.Core.Identity;

public sealed class TenantContext
{
    public required string TenantId { get; init; }

    public string? PlanSlug { get; init; }

    public IReadOnlyList<string> GrantedModels { get; init; } = Array.Empty<string>();
}
