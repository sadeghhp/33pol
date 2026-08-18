namespace Pol33.Core.Billing;

public static class UsageExportLimits
{
    /// <summary>
    /// Hard cap on ledger rows in one export. The exporter pages up to this many rows and then
    /// probes for one more, so the response flags truncation exactly even though a single query can
    /// never return more than <see cref="MaxEventPageSize"/> rows.
    /// </summary>
    public const int MaxEventRows = 5000;

    /// <summary>Hard cap on ledger rows in one page (repositories clamp any larger request).</summary>
    public const int MaxEventPageSize = 5000;
}
