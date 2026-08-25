namespace Pol33.Core.Models.Overview;

/// <summary>Who is using the gateway: top consumers and keys that need attention.</summary>
public sealed record TenantsOverview
{
    public DateTimeOffset BuiltAtUtc { get; init; }

    public string Currency { get; init; } = "USD";

    public int TenantCount { get; init; }

    public int KeyCount { get; init; }

    public int RevokedKeyCount { get; init; }

    public IReadOnlyList<TenantConsumer> TopConsumersMonthToDate { get; init; } = [];

    public IReadOnlyList<KeySummary> ExpiringKeys { get; init; } = [];

    public IReadOnlyList<KeySummary> IdleKeys { get; init; } = [];

    /// <summary>Share of month-to-date requests made without an API key (public-access models).</summary>
    public double AnonymousRequestShare { get; init; }
}

public sealed record TenantConsumer
{
    public Guid? TenantId { get; init; }

    public string? TenantSlug { get; init; }

    public string? PlanSlug { get; init; }

    public long Requests { get; init; }

    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public decimal Cost { get; init; }

    /// <summary>Requests in the last 24 hours, from the in-memory tenant activity ring (0 after a restart).</summary>
    public long Requests24h { get; init; }
}

public sealed record KeySummary
{
    public Guid Id { get; init; }

    public required string KeyPrefix { get; init; }

    public string? Label { get; init; }

    public string? TenantSlug { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }
}
