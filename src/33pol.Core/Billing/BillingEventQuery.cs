namespace Pol33.Core.Billing;

/// <summary>Filter for the billing ledger. Also used as the filter for ledger aggregation.</summary>
/// <param name="TenantId">Tenant filter; <see langword="null"/> applies no tenant filter.</param>
/// <param name="IncludeAnonymous">
/// Also match rows with no tenant (anonymous public-model traffic). See <see cref="UsageScope"/>.
/// </param>
/// <param name="CostCenter">Case-insensitive exact match on the cost centre.</param>
/// <param name="NoCostCenter">Match only rows with no cost centre; wins over <paramref name="CostCenter"/>.</param>
/// <param name="Cursor">Continue after a previous page; see <see cref="BillingEventCursor"/>.</param>
public sealed record BillingEventQuery(
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    Guid? TenantId = null,
    Guid? ApiKeyId = null,
    string? CostCenter = null,
    int Limit = 500,
    bool IncludeAnonymous = false,
    bool NoCostCenter = false,
    BillingEventCursor? Cursor = null)
{
    public UsageScope Scope => new(TenantId, IncludeAnonymous);
}
