using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IUsageRecorder
{
    void Enqueue(UsageEvent usageEvent);
}
