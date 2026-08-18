namespace Pol33.Core.Billing;

public static class UsageExportLimits
{
    /// <summary>Hard cap on ledger rows in one export; the response flags truncation.</summary>
    public const int MaxEventRows = 5000;

    /// <summary>Hard cap on ledger rows in one page.</summary>
    public const int MaxEventPageSize = 5000;
}
