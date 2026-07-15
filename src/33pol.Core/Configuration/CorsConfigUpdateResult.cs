namespace Pol33.Core.Configuration;

public sealed class CorsConfigUpdateResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public int StatusCode { get; init; } = 200;

    public static CorsConfigUpdateResult Ok(string message) =>
        new() { Success = true, Message = message, StatusCode = 200 };

    public static CorsConfigUpdateResult Fail(string message, int statusCode) =>
        new() { Success = false, Message = message, StatusCode = statusCode };
}
