using Microsoft.AspNetCore.Http;
using Pol33.Core.Identity;

namespace Pol33.Proxy.Routing;

/// <summary>
/// Resolves the key that rate limits, stream-concurrency slots and quotas are counted against.
/// </summary>
/// <remarks>
/// Authenticated traffic partitions by tenant. Unauthenticated traffic — which exists whenever any
/// model is marked <c>publicAccess</c> — used to collapse into a single literal <c>"anonymous"</c>
/// bucket shared by every caller on the internet, so one client could exhaust the RPM window and
/// every concurrent-stream slot and lock all other anonymous callers out. There is no credential to
/// partition on, so the remote address is used instead: it is not a strong identity, but it bounds
/// one client's impact to its own bucket instead of the whole anonymous tier.
/// </remarks>
public static class RateLimitPartition
{
    public const string AnonymousPrefix = "anon:";

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

        var remoteAddress = context.Connection.RemoteIpAddress;
        return remoteAddress is null
            ? UnknownAnonymousKey
            : AnonymousPrefix + remoteAddress.ToString();
    }

    public static bool IsAnonymous(string partitionKey) =>
        partitionKey.StartsWith(AnonymousPrefix, StringComparison.Ordinal);
}
