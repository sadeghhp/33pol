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
            // Unresolvable host: let the actual HTTP request fail naturally with its own DNS error.
            return null;
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

        return bytes[0] == 0;
    }

    private static bool IsBlockedIPv6(IPAddress address) =>
        address.IsIPv6LinkLocal ||
        address.IsIPv6UniqueLocal ||
        address.Equals(IPAddress.IPv6Any) ||
        IPAddress.IsLoopback(address);
}
