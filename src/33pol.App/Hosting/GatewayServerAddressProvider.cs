using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Pol33.Core.Abstractions;

namespace Pol33.App.Hosting;

/// <summary>
/// Reads the addresses Kestrel actually bound and compares an upstream URL against them.
/// </summary>
/// <remarks>
/// The addresses are only known once the server has started, so they are read lazily and cached on
/// the first non-empty answer. Until then <see cref="IsSelf"/> is false: an unknown listener must
/// never be grounds for condemning a backend.
/// </remarks>
public sealed class GatewayServerAddressProvider(IServer server) : IGatewaySelfAddressProvider
{
    private IReadOnlyList<Uri>? _listeners;

    public bool IsSelf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var target))
        {
            return false;
        }

        foreach (var listener in GetListeners())
        {
            if (Matches(listener, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Same port, and a host that is either literally the same or a loopback name answered by a
    /// wildcard binding. Port is always compared: a genuine upstream on the same machine but a
    /// different port is a normal deployment, and condemning it would be worse than the bug.
    /// </summary>
    private static bool Matches(Uri listener, Uri target)
    {
        if (listener.Port != target.Port)
        {
            return false;
        }

        if (IsWildcard(listener.Host))
        {
            // The listener answers on every interface, so any address that reaches this machine is
            // this gateway. Only loopback can be asserted without resolving DNS, which a health
            // sweep must not do on the hot path.
            return IsLoopback(target.Host);
        }

        return string.Equals(listener.Host, target.Host, StringComparison.OrdinalIgnoreCase) ||
               (IsLoopback(listener.Host) && IsLoopback(target.Host));
    }

    private static bool IsWildcard(string host) =>
        host is "+" or "*" or "0.0.0.0" or "[::]" or "::";

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host is "127.0.0.1" or "::1" or "[::1]" ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private IReadOnlyList<Uri> GetListeners()
    {
        if (_listeners is { Count: > 0 })
        {
            return _listeners;
        }

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
        {
            return [];
        }

        var parsed = new List<Uri>(addresses.Count);
        foreach (var address in addresses)
        {
            // Kestrel reports wildcards as http://+:8080 / http://[::]:8080, which Uri rejects or
            // mangles; normalise the host to something parseable and keep the wildcard meaning.
            var normalized = address.Replace("://+:", "://0.0.0.0:", StringComparison.Ordinal)
                                    .Replace("://*:", "://0.0.0.0:", StringComparison.Ordinal);
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                parsed.Add(uri);
            }
        }

        _listeners = parsed;
        return parsed;
    }
}
