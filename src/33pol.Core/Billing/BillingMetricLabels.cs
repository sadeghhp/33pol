namespace Pol33.Core.Billing;

/// <summary>
/// Label values shared by the billing persistence path and the FinOps Prometheus exporter.
/// Tenant slug is the only identity label; a raw API key, key hash, or key prefix is never one.
/// </summary>
public static class BillingMetricLabels
{
    public const string AnonymousTenant = "anonymous";

    public const string UnknownTenant = "unknown";

    public const string NoCostCenter = "(none)";

    public const string UnlabeledKey = "(unlabeled)";

    public const string NoAssignee = "(none)";

    /// <summary>How many API keys, by month-to-date cost, are exported as their own series.</summary>
    public const int TopKeyLimit = 25;
}
