namespace Pol33.Core.Models;

public sealed class BudgetCheckResult
{
    public bool IsAllowed { get; init; }

    public string? BudgetName { get; init; }

    public static BudgetCheckResult Allowed { get; } = new() { IsAllowed = true };

    public static BudgetCheckResult HardExceeded(string budgetName) => new()
    {
        IsAllowed = false,
        BudgetName = budgetName,
    };
}
