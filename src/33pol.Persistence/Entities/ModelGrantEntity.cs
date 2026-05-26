using Pol33.Core.Identity;

namespace Pol33.Persistence.Entities;

public sealed class ModelGrantEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public TenantEntity Tenant { get; set; } = null!;

    public string ModelPattern { get; set; } = string.Empty;

    public GrantEffect Effect { get; set; } = GrantEffect.Allow;
}
