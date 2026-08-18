namespace Pol33.Core.Models;

/// <summary>
/// Month-end projection: what has been spent this UTC month so far, plus the average of the last
/// <see cref="TrailingDays"/> complete UTC days for each day remaining.
/// </summary>
public sealed class UsageForecastResponse
{
    public required int TrailingDays { get; init; }

    /// <summary>First complete day in the trailing window (UTC).</summary>
    public DateOnly? WindowStart { get; init; }

    /// <summary>Last complete day in the trailing window — yesterday (UTC).</summary>
    public DateOnly? WindowEnd { get; init; }

    public required decimal TrailingTotalCost { get; init; }

    public decimal AverageDailyCost { get; init; }

    public decimal MonthToDateCost { get; init; }

    /// <summary>Days of the current UTC month still to come, today excluded.</summary>
    public int DaysRemainingInMonth { get; init; }

    public required decimal ProjectedMonthlyCost { get; init; }

    public required string Currency { get; init; }
}
