namespace Pol33.Core.Models;

public sealed class RegistryMutationResult
{
    public bool Success { get; init; }

    public required string Message { get; init; }

    public int SuggestedStatusCode { get; init; } = 500;

    public static RegistryMutationResult Ok(string message) =>
        new() { Success = true, Message = message, SuggestedStatusCode = 200 };

    public static RegistryMutationResult Fail(string message, int suggestedStatusCode = 400) =>
        new() { Success = false, Message = message, SuggestedStatusCode = suggestedStatusCode };
}
