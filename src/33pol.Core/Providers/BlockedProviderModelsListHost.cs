using System.Net;
using System.Net.Sockets;

namespace Pol33.Core.Providers;

internal static class BlockedProviderModelsListHost
{
    private const string BlockedAddressError =
        "modelsUrl must not target private, link-local, loopback, unspecified, or metadata addresses.";

    public static bool IsBlocked(Uri uri, out string? error)
    {
        error = null;
        var host = uri.Host;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            error = "modelsUrl must not target localhost.";
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        if (IsBlockedAddress(address))
        {
            error = BlockedAddressError;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a DNS hostname and rejects the request if any resolved address is private/internal.
    /// This closes the SSRF bypass where a public-looking hostname resolves to an internal or
    /// cloud-metadata address. Literal-IP and localhost hosts are handled synchronously by <see
    /// cref="IsBlocked"/>. Returns an error string when blocked, otherwise <c>null</c>.
    /// </summary>
    public static async Task<string?> ValidateResolvedHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (IsBlocked(uri, out var error))
        {
            return error;
        }

        // Literal IPs are already validated by IsBlocked; only DNS names need resolution.
        if (IPAddress.TryParse(uri.Host, out _))
        {
            return null;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Fail closed. Letting an unresolvable host through "so the HTTP request fails
            // naturally" assumed both resolvers agree — but a host form this resolver rejects and
            // the socket layer accepts would then reach the connection completely unchecked.
            return $"modelsUrl host '{uri.Host}' could not be resolved.";
        }

        if (addresses.Length == 0)
        {
            return $"modelsUrl host '{uri.Host}' did not resolve to any address.";
        }

        foreach (var address in addresses)
        {
            if (IsBlockedAddress(address))
            {
                return BlockedAddressError;
            }
        }

        return null;
    }

    internal static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedIPv4(address),
            AddressFamily.InterNetworkV6 => IsBlockedIPv6(address),
            _ => true,
        };
    }

    /// <summary>
    /// IPv6 addresses that embed an IPv4 address are judged by that IPv4 address as well: NAT64
    /// (<c>64:ff9b::/96</c>, how an IPv6-only cluster reaches IPv4 — including the metadata
    /// service), IPv4-compatible (<c>::a.b.c.d</c>), 6to4 (<c>2002::/16</c>) and Teredo
    /// (<c>2001::/32</c>, whose embedded server address is the reachable IPv4 host). Site-local and
    /// multicast are never legitimate upstream targets either.
    /// </summary>
    private static bool IsBlockedIPv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal ||
            address.IsIPv6UniqueLocal ||
            address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast ||
            address.Equals(IPAddress.IPv6Any) ||
            IPAddress.IsLoopback(address))
        {
            return true;
        }

        var embedded = TryExtractEmbeddedIPv4(address);
        return embedded is not null && IsBlockedIPv4(embedded);
    }

    private static IPAddress? TryExtractEmbeddedIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 16)
        {
            return null;
        }

        // NAT64 well-known prefix 64:ff9b::/96 — IPv4 in the last four bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b &&
            b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
            b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
        {
            return new IPAddress(b[12..16]);
        }

        // IPv4-compatible ::a.b.c.d (deprecated but still routed by some stacks) — first 12 bytes zero.
        // "::" itself and "::1" are handled above; anything else here carries an IPv4 payload.
        if (b[..12].All(static x => x == 0))
        {
            return new IPAddress(b[12..16]);
        }

        // 6to4 2002:AABB:CCDD::/48 — IPv4 in bytes 2..5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return new IPAddress(b[2..6]);
        }

        // Teredo 2001:0000:SERVER-IPv4:flags:~port:~client-IPv4. Both the server (bytes 4..7,
        // plain) and the client (bytes 12..15, bitwise NOT) are reachable IPv4 hosts; either being
        // internal is disqualifying.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
        {
            var client = new IPAddress([(byte)~b[12], (byte)~b[13], (byte)~b[14], (byte)~b[15]]);
            if (IsBlockedIPv4(client))
            {
                return client;
            }

            return new IPAddress(b[4..8]);
        }

        return null;
    }

    private static bool IsBlockedIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 10)
        {
            return true;
        }

        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return true;
        }

        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }

        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        // 100.64.0.0/10 — carrier-grade NAT, and the range managed Kubernetes offerings routinely
        // use for pod and service networks, which makes it internal in exactly the deployments this
        // gateway targets.
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            return true;
        }

        // 192.0.0.0/24 (IETF protocol assignments) and 198.18.0.0/15 (benchmarking) are neither
        // routable nor legitimate upstream targets.
        if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
        {
            return true;
        }

        if (bytes[0] == 198 && bytes[1] is 18 or 19)
        {
            return true;
        }

        return bytes[0] == 0;
    }
}
