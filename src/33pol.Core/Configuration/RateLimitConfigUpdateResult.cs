namespace Pol33.Core.Configuration;

/// <summary>
/// Thrown when a rate-limit write was based on a configuration version that is no longer current.
/// </summary>
/// <remarks>
/// The rule set is replaced wholesale — a partial update gives no way to delete a rule — so a write
/// based on a stale read does not merge with what landed in between, it erases it. Two operators with
/// the Rate limits page open would each save their own complete set and the second would win
/// silently, including for security-relevant rules like <c>auth_failure</c>. The same shape as
/// <see cref="Pol33.Core.Models.ModelRouteVersionConflictException"/>, which guards the route table
/// against exactly this.
/// </remarks>
public sealed class RateLimitVersionConflictException(long expectedVersion, long actualVersion)
    : InvalidOperationException(
        $"Rate-limit configuration changed since it was read (expected version {expectedVersion}, "
        + $"found {actualVersion}).")
{
    public long ExpectedVersion { get; } = expectedVersion;

    public long ActualVersion { get; } = actualVersion;
}

public sealed class RateLimitConfigUpdateResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public int StatusCode { get; init; } = 200;

    /// <summary>
    /// The rate-limit configuration version the write produced, from the repository rather than from
    /// the refreshed snapshot: the write is committed even when the refresh that follows it is not,
    /// and the caller's next write has to be based on this number either way.
    /// </summary>
    public long? Version { get; init; }

    public static RateLimitConfigUpdateResult Ok(string message, long? version = null) =>
        new() { Success = true, Message = message, StatusCode = 200, Version = version };

    public static RateLimitConfigUpdateResult Fail(string message, int statusCode) =>
        new() { Success = false, Message = message, StatusCode = statusCode };
}
