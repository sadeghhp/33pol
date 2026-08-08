using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IQuotaService
{
    QuotaCheckResult CheckBeforeForward(string partitionKey, string modelId);

    /// <param name="occurredAt">
    /// When the usage was actually incurred. Usage is committed asynchronously, so an event that
    /// crosses the month boundary while queued must still count against the month it belongs to;
    /// attributing it to the commit-time clock moved it into the new month's fresh allowance.
    /// </param>
    void CommitUsage(
        string partitionKey,
        string modelId,
        long totalTokens,
        string requestId,
        DateTimeOffset? occurredAt = null);
}
