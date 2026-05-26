using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IQuotaService
{
    QuotaCheckResult CheckBeforeForward(string partitionKey, string modelId);

    void CommitUsage(string partitionKey, string modelId, long totalTokens, string requestId);
}
