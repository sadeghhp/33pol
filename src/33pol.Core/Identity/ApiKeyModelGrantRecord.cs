namespace Pol33.Core.Identity;

public sealed record ApiKeyModelGrantRecord(
    Guid Id,
    Guid ApiKeyId,
    string ModelPattern,
    GrantEffect Effect);
