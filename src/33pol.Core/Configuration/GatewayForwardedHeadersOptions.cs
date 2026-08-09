using System.Net;

namespace Pol33.Core.Configuration;

/// <summary>
/// Whether, and from whom, the gateway trusts the <c>X-Forwarded-*</c> headers a reverse proxy
/// writes.
/// </summary>
/// <remarks>
/// Anonymous rate limits, quotas and stream-concurrency slots are partitioned by client IP (see
/// <c>RateLimitPartition</c>). Behind an ingress, a load balancer, or docker's userland proxy every
/// request arrives from the proxy's own address, so without this the whole anonymous tier collapses
/// into a single bucket: one caller of a <c>publicAccess</c> model exhausts the RPM window and every
/// stream slot for everyone else reaching that deployment.
///
/// Off by default, and never inferred from the environment. <c>X-Forwarded-For</c> is written by
/// whoever sent the request, so honouring it from an untrusted peer is strictly worse than ignoring
/// it — a caller could put a fresh fake address on every request and mint an unlimited number of
/// partitions, bypassing anonymous rate limiting entirely rather than merely sharing it. Enable it
/// only when a proxy the operator controls overwrites the header, and name that proxy in
/// <see cref="KnownProxies"/> or <see cref="KnownNetworks"/>.
/// </remarks>
public sealed class GatewayForwardedHeadersOptions
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// When true, <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> received from a trusted peer
    /// replace the connection's remote address and scheme.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Individual proxy addresses whose forwarded headers are trusted. Loopback is trusted by
    /// default, which covers a reverse proxy running on the same host.
    /// </summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>
    /// Proxy networks in CIDR form (for example <c>10.0.0.0/8</c>) whose forwarded headers are
    /// trusted. Host bits are masked off, so <c>10.0.0.1/8</c> is read as <c>10.0.0.0/8</c>.
    /// </summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// How many proxy hops to walk back through — set it to the number of trusted proxies in front
    /// of the gateway, no more. Each hop consumes one entry from the right of the header, so a limit
    /// larger than the real chain lets the client's own spoofed entries be read as the origin.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// Trust forwarded headers from any peer. Only safe when nothing but a trusted proxy can reach
    /// the gateway's port at all, since it lets a direct caller choose the address every anonymous
    /// limit is counted against.
    /// </summary>
    public bool TrustAllProxies { get; set; }

    public IReadOnlyList<IPAddress> GetKnownProxies() =>
        Normalize(KnownProxies)
            .Select(static entry => IPAddress.TryParse(entry, out var address) ? address : null)
            .Where(static address => address is not null)
            .Select(static address => address!)
            .ToList();

    public IReadOnlyList<IPNetwork> GetKnownNetworks()
    {
        var networks = new List<IPNetwork>();
        foreach (var entry in Normalize(KnownNetworks))
        {
            if (IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
            }
        }

        return networks;
    }

    /// <summary>
    /// True when the settings trust nothing beyond the framework's loopback default, so a proxy on
    /// another host would have its headers ignored and the fix would silently not apply.
    /// </summary>
    public bool HasNoExplicitTrustAnchors =>
        !TrustAllProxies && GetKnownProxies().Count == 0 && GetKnownNetworks().Count == 0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var prefix = $"{SectionName}.";

        foreach (var entry in Normalize(KnownProxies))
        {
            if (!IPAddress.TryParse(entry, out _))
            {
                errors.Add($"{prefix}{nameof(KnownProxies)} contains '{entry}', which is not a valid IP address.");
            }
        }

        foreach (var entry in Normalize(KnownNetworks))
        {
            if (!IPNetwork.TryParse(entry, out _))
            {
                errors.Add(
                    $"{prefix}{nameof(KnownNetworks)} contains '{entry}', which is not a valid CIDR network. "
                    + "Use an address and prefix length, for example '10.0.0.0/8'.");
            }
        }

        if (ForwardLimit < 1)
        {
            errors.Add($"{prefix}{nameof(ForwardLimit)} must be at least 1.");
        }

        return errors;
    }

    private static IEnumerable<string> Normalize(IEnumerable<string>? entries) =>
        (entries ?? [])
            .Select(static entry => entry?.Trim() ?? string.Empty)
            .Where(static entry => entry.Length > 0);
}
