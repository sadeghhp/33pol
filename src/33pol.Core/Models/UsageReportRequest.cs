using Pol33.Core.Billing;

namespace Pol33.Core.Models;

public sealed class UsageReportRequest
{
    public DateOnly? FromDate { get; init; }

    public DateOnly? ToDate { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>Also include anonymous (no-tenant) usage. See <see cref="UsageScope"/>.</summary>
    public bool IncludeAnonymous { get; init; }

    /// <summary>Case-insensitive exact match.</summary>
    public string? CostCenter { get; init; }

    /// <summary>Only rows with no cost centre; wins over <see cref="CostCenter"/>.</summary>
    public bool NoCostCenter { get; init; }

    /// <summary>
    /// When set, the report is aggregated from the billing ledger for this key instead of the daily
    /// rollup table, which has no per-key dimension.
    /// </summary>
    public Guid? ApiKeyId { get; init; }

    public UsageScope Scope => new(TenantId, IncludeAnonymous);
}
