namespace Pol33.Core.Billing;

public sealed record BillingEventQuery(
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    Guid? TenantId = null,
    int Limit = 500);
