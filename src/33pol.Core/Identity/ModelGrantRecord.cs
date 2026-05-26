namespace Pol33.Core.Identity;

public sealed record ModelGrantRecord(
    Guid Id,
    Guid TenantId,
    string ModelPattern,
    GrantEffect Effect);
