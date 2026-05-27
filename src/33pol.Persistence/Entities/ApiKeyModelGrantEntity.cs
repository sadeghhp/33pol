using Pol33.Core.Identity;

namespace Pol33.Persistence.Entities;

public sealed class ApiKeyModelGrantEntity
{
    public Guid Id { get; set; }

    public Guid ApiKeyId { get; set; }

    public ApiKeyEntity ApiKey { get; set; } = null!;

    public required string ModelPattern { get; set; }

    public GrantEffect Effect { get; set; } = GrantEffect.Allow;
}
