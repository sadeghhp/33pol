namespace Pol33.Core.Models;

public sealed class QuotaCheckResult
{
    public bool IsAllowed { get; init; }

    public bool IsSoftWarning { get; init; }

    public string? WarningMessage { get; init; }

    public static QuotaCheckResult Allowed { get; } = new() { IsAllowed = true };

    public static QuotaCheckResult HardExceeded { get; } = new() { IsAllowed = false };

    public static QuotaCheckResult SoftWarning(string message) => new()
    {
        IsAllowed = true,
        IsSoftWarning = true,
        WarningMessage = message,
    };
}
