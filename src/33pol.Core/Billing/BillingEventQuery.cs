namespace Pol33.Core.Billing;

public sealed record BillingEventQuery(
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    Guid? TenantId = null,
    Guid? ApiKeyId = null,
    string? CostCenter = null,
    int Limit = 500);
