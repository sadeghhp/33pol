using System.Net;
using System.Net.Sockets;

namespace Pol33.Core.Providers;

internal static class BlockedProviderModelsListHost
{
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
            error = "modelsUrl must not target private, link-local, loopback, or metadata addresses.";
            return true;
        }

        return false;
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
        IPAddress.IsLoopback(address);
}
