using Pol33.Core.Models.Overview;

namespace Pol33.Core.Abstractions;

/// <summary>The outcome of the most recent billing reconciliation sweep, for the admin Overview.</summary>
public interface IBillingReconciliationStateSource
{
    ReconciliationStatus Current { get; }
}
