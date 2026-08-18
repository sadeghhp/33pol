using Pol33.Core.Billing;

namespace Pol33.Core.Models;

public sealed class UsageForecastRequest
{
    public required UsageScope Scope { get; init; }

    public string? CostCenter { get; init; }

    public bool NoCostCenter { get; init; }

    /// <summary>When set, spend is aggregated from the ledger for this key only.</summary>
    public Guid? ApiKeyId { get; init; }

    public int TrailingDays { get; init; } = 7;
}
