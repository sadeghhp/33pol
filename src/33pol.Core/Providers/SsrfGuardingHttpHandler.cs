using System.Net;
using System.Net.Sockets;

namespace Pol33.Core.Providers;

/// <summary>
/// Delegating handler that re-validates the target host of every outbound request against the
/// provider-discovery blocklist, resolving DNS names to their addresses first. Paired with
/// <c>AllowAutoRedirect = false</c> on the primary handler, this prevents SSRF via a public-looking
/// hostname that resolves to an internal address or a redirect to an internal target.
/// </summary>
/// <remarks>
/// This is the outer of two checks and is <em>advisory</em>: it fails fast with a clear message, but
/// it resolves DNS separately from the socket layer, so a record that answers differently on the
/// second lookup would slip past it. <see cref="CreateGuardedPrimaryHandler"/> is the authoritative
/// one — it validates the address the connection is actually opened to, which cannot be raced.
/// </remarks>
public sealed class SsrfGuardingHttpHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri)
        {
            var error = await BlockedProviderModelsListHost
                .ValidateResolvedHostAsync(uri, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                throw new HttpRequestException(error);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the primary handler that enforces the address blocklist at connect time.
    /// </summary>
    /// <remarks>
    /// Validating a resolved address and then letting the socket layer resolve the name again is a
    /// time-of-check/time-of-use gap: an attacker-controlled record with a very low TTL can return a
    /// public address to the check and a loopback or metadata address to the connection. Checking
    /// inside <see cref="SocketsHttpHandler.ConnectCallback"/> closes it, because the endpoint
    /// inspected there is the one the connection is opened to.
    /// </remarks>
    public static SocketsHttpHandler CreateGuardedPrimaryHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = static async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                var addresses = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

                // Fail closed: if nothing resolves, there is nothing to validate and nothing to
                // connect to either. Returning success here previously let an address form the
                // gateway's resolver rejected but the socket layer accepted through unchecked.
                if (addresses.Length == 0)
                {
                    throw new HttpRequestException($"Could not resolve '{host}'.");
                }

                foreach (var address in addresses)
                {
                    if (BlockedProviderModelsListHost.IsBlockedAddress(address))
                    {
                        throw new HttpRequestException(
                            $"Refusing to connect to '{host}': it resolves to the blocked address {address}.");
                    }
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, port, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
}
