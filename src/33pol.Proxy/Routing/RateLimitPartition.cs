using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Identity;
using Pol33.Core.RateLimiting;

namespace Pol33.Proxy.Routing;

/// <summary>
/// Resolves the identity that rate limits, stream-concurrency slots and quotas are counted against.
/// </summary>
/// <remarks>
/// Authenticated traffic partitions by tenant, and — where the operator has configured per-key rules
/// — additionally by API key, so one runaway credential can be bounded without bounding its
/// siblings.
///
/// Unauthenticated traffic — which exists whenever any model is marked <c>publicAccess</c> — used to
/// collapse into a single literal <c>"anonymous"</c> bucket shared by every caller on the internet,
/// so one client could exhaust the RPM window and every concurrent-stream slot and lock all other
/// anonymous callers out. There is no credential to partition on, so the client's address block is
/// used instead: it is not a strong identity, but it bounds one client's impact to its own bucket
/// instead of the whole anonymous tier.
/// </remarks>
public static class RateLimitPartition
{
    public const string AnonymousPrefix = "anon:";

    /// <summary>
    /// Namespace for the budget that requests failing authentication are counted against, keyed by
    /// client address block. Kept separate from <see cref="AnonymousPrefix"/> so a flood of bad
    /// credentials from one address cannot also exhaust that address's public-model allowance —
    /// the two are bounded independently.
    /// </summary>
    public const string AuthFailurePrefix = "authfail:";

    /// <summary>Used when the connection has no remote address (in-memory test servers, unix sockets).</summary>
    public const string UnknownAnonymousKey = AnonymousPrefix + "unknown";

    /// <summary>
    /// Prefix length an IPv6 client is collapsed to.
    /// </summary>
    /// <remarks>
    /// An IPv6 client is routinely handed a /64 or shorter, and often a /48 or /56 for a whole site.
    /// Keyed on the full 128-bit address, one such client can mint 2^64 distinct partitions at will:
    /// each gets its own full bucket, so the limit never binds, and the churn walks the partition
    /// table into its ceiling and evicts the buckets of legitimate callers — which resets those
    /// buckets too. /64 is the smallest block that is reliably one subscriber, so it is the unit that
    /// makes the limit mean something.
    /// </remarks>
    public const int IPv6PrefixLength = 64;

    public static string Resolve(HttpContext context)
    {
        if (TryGetTenant(context, out var tenant))
        {
            return tenant.TenantId;
        }

        return AnonymousPrefix + ResolveClientAddress(context);
    }

    /// <summary>
    /// Everything the rule set is built from, read once so the middlewares that need it do not each
    /// re-derive it from <see cref="HttpContext.Items"/>.
    /// </summary>
    public static RateLimitSubject ResolveSubject(HttpContext context)
    {
        if (TryGetTenant(context, out var tenant))
        {
            return new RateLimitSubject(
                tenant.TenantId,
                tenant.PlanSlug,
                string.IsNullOrEmpty(tenant.ApiKeyId) ? null : tenant.ApiKeyId,
                tenant.TenantId);
        }

        return new RateLimitSubject(null, null, null, AnonymousPrefix + ResolveClientAddress(context));
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

    private static bool TryGetTenant(HttpContext context, out TenantContext tenant)
    {
        if (context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var value) &&
            value is TenantContext resolved &&
            !string.IsNullOrWhiteSpace(resolved.TenantId))
        {
            tenant = resolved;
            return true;
        }

        tenant = null!;
        return false;
    }

    private static string ResolveClientAddress(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        return Normalize(address);
    }

    /// <summary>
    /// The address block a limit is counted against: the full address for IPv4, the
    /// <see cref="IPv6PrefixLength"/> prefix for IPv6.
    /// </summary>
    internal static string Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            // A dual-stack socket reports an IPv4 client as ::ffff:a.b.c.d. Left alone, the same
            // client would land in a different bucket depending on how the listener was bound.
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written) || written != 16)
        {
            return address.ToString();
        }

        // Zero everything below the prefix so every address in the block produces one key. The
        // scope id (a link-local "%3" suffix) is dropped with it, which is correct: it is local to
        // the receiving host and says nothing about who the caller is.
        bytes[(IPv6PrefixLength / 8)..].Clear();
        return new IPAddress(bytes).ToString() + "/" + IPv6PrefixLength;
    }
}
