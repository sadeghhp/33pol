namespace Pol33.Core.Models;

public sealed class ConfigReloadResult
{
    public required string Status { get; init; }

    public required string Message { get; init; }

    public int? PreviousModelCount { get; init; }

    public int? CurrentModelCount { get; init; }

    public IReadOnlyList<string>? Models { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public int SuggestedStatusCode { get; init; } = 200;

    public static ConfigReloadResult Success(
        string message,
        int previousModelCount,
        int currentModelCount,
        IReadOnlyList<string> models) =>
        new()
        {
            Status = "success",
            Message = message,
            PreviousModelCount = previousModelCount,
            CurrentModelCount = currentModelCount,
            Models = models,
            Timestamp = DateTimeOffset.UtcNow,
            SuggestedStatusCode = 200,
        };

    public static ConfigReloadResult Error(string message, int suggestedStatusCode = 500) =>
        new()
        {
            Status = "error",
            Message = message,
            SuggestedStatusCode = suggestedStatusCode,
        };
}
