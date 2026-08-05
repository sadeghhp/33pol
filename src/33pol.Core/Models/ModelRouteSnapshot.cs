namespace Pol33.Core.Models;

/// <summary>
/// The persisted route table plus the version it was read at. Mutations echo the version back on
/// write so a concurrent writer (another admin, another replica) is detected instead of silently
/// overwritten — the route table is rewritten wholesale, so a lost update loses whole routes.
/// </summary>
public sealed record ModelRouteSnapshot(IReadOnlyList<ModelConfig> Models, long Version)
{
    public static ModelRouteSnapshot Empty { get; } = new([], 0);
}

/// <summary>
/// Thrown when a route write is attempted against a version that is no longer current, i.e. the
/// route table changed between the caller's read and its write.
/// </summary>
public sealed class ModelRouteVersionConflictException(long expectedVersion, long actualVersion)
    : Exception(
        $"The model routes changed since they were read (expected version {expectedVersion}, " +
        $"found {actualVersion}).")
{
    public long ExpectedVersion { get; } = expectedVersion;

    public long ActualVersion { get; } = actualVersion;
}
