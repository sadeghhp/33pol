namespace Pol33.Core.Identity;

public sealed record ApiKeyMetadataUpdate(
    string? Label,
    string? Assignee,
    string? Description,
    string? CostCenter);
