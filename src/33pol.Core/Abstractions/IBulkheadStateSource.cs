namespace Pol33.Core.Abstractions;

/// <summary>Per-model bulkhead occupancy for the admin Overview: how full each model's forwarding slots and wait queue are.</summary>
public interface IBulkheadStateSource
{
    IReadOnlyList<BulkheadModelState> GetStates();
}

public sealed record BulkheadModelState(string ModelId, int InFlight, int Queued, int MaxConcurrent, int MaxQueued);
