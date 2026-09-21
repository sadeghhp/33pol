namespace Pol33.Persistence.Repositories;

/// <summary>
/// The rows of <c>ConfigVersions</c> that more than one class has to agree on.
/// </summary>
/// <remarks>
/// A version row is a concurrency domain: a write names the version it was based on and is refused
/// when the row has moved. Rate limits shared the general row with CORS, so saving a CORS origin made
/// a pending rate-limit write look stale — "changed by someone else" when nobody had touched a rule —
/// and the rule set is replaced wholesale, so the only way out was to throw the draft away. Rate
/// limits now have a row of their own, the way the route table (row 2) already does.
/// </remarks>
internal static class ConfigVersionRows
{
    /// <summary>
    /// The general version. Every write that changes the configuration snapshot bumps it, because it
    /// is what the reconcile poll on the other instances watches to decide whether to reload.
    /// </summary>
    public const int General = 1;

    /// <summary>The rate-limit configuration's own version: what its ETag names and If-Match is checked against.</summary>
    public const int RateLimits = 3;
}
