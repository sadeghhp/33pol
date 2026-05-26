namespace Pol33.Core.Billing;

public sealed record BillingCostBreakdown(
    decimal InputCost,
    decimal OutputCost,
    decimal TotalCost,
    string Currency);
