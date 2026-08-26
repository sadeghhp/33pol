using Microsoft.AspNetCore.Http;
using Pol33.Core.Identity;

namespace Pol33.Proxy.Routing;

/// <summary>
/// Resolves the key that rate limits, stream-concurrency slots and quotas are counted against.
/// </summary>
/// <remarks>
/// Authenticated traffic partitions by tenant — every API key a tenant holds draws on the same
/// budget, so a tier is a tenant-wide allowance and one busy key can crowd out its siblings.
///
/// Unauthenticated traffic — which exists whenever any model is marked <c>publicAccess</c> — used to
/// collapse into a single literal <c>"anonymous"</c> bucket shared by every caller on the internet,
/// so one client could exhaust the RPM window and every concurrent-stream slot and lock all other
/// anonymous callers out. There is no credential to partition on, so the remote address is used
/// instead: it is not a strong identity, but it bounds one client's impact to its own bucket instead
/// of the whole anonymous tier.
/// </remarks>
public static class RateLimitPartition
{
    public const string AnonymousPrefix = "anon:";

    /// <summary>
    /// Namespace for the budget that requests failing authentication are counted against, keyed by
    /// client address. Kept separate from <see cref="AnonymousPrefix"/> so a flood of bad
    /// credentials from one address cannot also exhaust that address's public-model allowance —
    /// the two are bounded independently.
    /// </summary>
    public const string AuthFailurePrefix = "authfail:";

    /// <summary>Used when the connection has no remote address (in-memory test servers, unix sockets).</summary>
    public const string UnknownAnonymousKey = AnonymousPrefix + "unknown";

    public static string Resolve(HttpContext context)
    {
        if (context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var value) &&
            value is TenantContext tenant &&
            !string.IsNullOrWhiteSpace(tenant.TenantId))
        {
            return tenant.TenantId;
        }

        return AnonymousPrefix + ResolveClientAddress(context);
    }

    /// <summary>
    /// The auth-failure partition for this connection. Deliberately address-only: it is evaluated
    /// before authentication has run, so there is no credential to key on — and keying on the
    /// presented credential would let an attacker mint a fresh partition per guess.
    /// </summary>
    public static string ResolveAuthFailure(HttpContext context) =>
        AuthFailurePrefix + ResolveClientAddress(context);

    public static bool IsAnonymous(string partitionKey) =>
        partitionKey.StartsWith(AnonymousPrefix, StringComparison.Ordinal);

    private static string ResolveClientAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
