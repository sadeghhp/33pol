namespace Pol33.Core.Models;

public sealed class UsageForecastResponse
{
    public required int TrailingDays { get; init; }

    public required decimal TrailingTotalCost { get; init; }

    public required decimal ProjectedMonthlyCost { get; init; }

    public required string Currency { get; init; }
}
