namespace Pol33.Core.Models.Overview;

/// <summary>In-memory policy pressure: who is being throttled, by which control, in the last hour and day.</summary>
public sealed record PolicyLiveOverview
{
    public IReadOnlyList<CountRow> RejectionsByReason1h { get; init; } = [];

    public IReadOnlyList<CountRow> RejectionsByReason24h { get; init; } = [];

    public IReadOnlyList<CountRow> RejectionsByTenant1h { get; init; } = [];

    public IReadOnlyList<CountRow> RejectionsByModel1h { get; init; } = [];

    /// <summary>Model names clients asked for that the registry does not know.</summary>
    public IReadOnlyList<CountRow> UnknownModels1h { get; init; } = [];

    public IReadOnlyList<CountRow> GrantDenials1h { get; init; } = [];

    public IReadOnlyList<CountRow> BudgetRejections1h { get; init; } = [];
}

/// <summary>Database-backed policy state: quota consumption and budgets per tenant.</summary>
public sealed record PolicyOverview
{
    public DateTimeOffset BuiltAtUtc { get; init; }

    public IReadOnlyList<QuotaStatus> Quotas { get; init; } = [];

    public IReadOnlyList<BudgetStatus> BudgetsNearLimit { get; init; } = [];

    public IReadOnlyList<CountRow> GrantDenials { get; init; } = [];

    public IReadOnlyList<CountRow> UnknownModels { get; init; } = [];
}

public sealed record CountRow(string Key, long Count, string? Label = null);

public sealed record QuotaStatus
{
    public required string PartitionKey { get; init; }

    public string? TenantSlug { get; init; }

    public string? PlanSlug { get; init; }

    public string Period { get; init; } = string.Empty;

    public long Used { get; init; }

    public long Limit { get; init; }

    public double Ratio { get; init; }

    public bool NearLimit { get; init; }

    public bool Exceeded { get; init; }
}
