using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IUsageRecorder
{
    /// <summary>
    /// Hands a usage event to the persistence pipeline.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the event was accepted for persistence; <c>false</c> when it was dropped
    /// (the queue is saturated) and nothing downstream will ever see it.
    /// </returns>
    /// <remarks>
    /// The result is load-bearing, not advisory. The router settles a request's budget reservation
    /// only when persistence will run; treating a dropped event as accepted leaked the reservation
    /// for its full TTL, so sustained load accumulated phantom spend and hard-stopped tenants that
    /// were nowhere near their budget.
    /// </remarks>
    bool Enqueue(UsageEvent usageEvent);
}
