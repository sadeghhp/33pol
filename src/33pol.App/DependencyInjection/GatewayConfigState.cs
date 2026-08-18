using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.App.DependencyInjection;

/// <summary>
/// Holds the current configuration snapshot behind a lock-free volatile reference. The syncer swaps
/// in a new immutable snapshot with <see cref="Set"/>; the hot path reads <see cref="Current"/>
/// without locking and never sees a torn update. Starts at the appsettings-derived snapshot so
/// reads are safe before the first database load.
///
/// <para>Environment CORS origins (<c>GATEWAY_CORS_ALLOWED_ORIGIN_*</c> /
/// <c>GATEWAY_CORS_ALLOWED_ORIGINS</c>) are overlaid on <em>every</em> snapshot that passes through
/// here — the initial one and each database load. Before, they were only written into the options
/// copy that seeds the database on first boot, so a database deployment ignored them from the
/// second boot on and a database-less one ignored them always, while the operator docs promised
/// that editing <c>.env</c> and recreating the container was enough.</para>
/// </summary>
internal sealed class GatewayConfigState : IGatewayConfigProvider
{
    private readonly IReadOnlyList<string> _environmentOrigins;
    private volatile GatewayConfigSnapshot _current;

    public GatewayConfigState(GatewayConfigSnapshot initial)
        : this(initial, [])
    {
    }

    /// <param name="initial">The appsettings-derived snapshot.</param>
    /// <param name="environmentOrigins">
    /// Origins from the environment, already normalized; merged ahead of the snapshot's own list.
    /// </param>
    public GatewayConfigState(GatewayConfigSnapshot initial, IReadOnlyList<string> environmentOrigins)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(environmentOrigins);
        _environmentOrigins = environmentOrigins;
        _current = Overlay(initial);
    }

    public GatewayConfigSnapshot Current => _current;

    public void Set(GatewayConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _current = Overlay(snapshot);
    }

    /// <summary>
    /// Environment origins first, then the snapshot's, de-duplicated. A union rather than a
    /// replacement so an origin added through the admin console is not silently dropped on the
    /// next boot of a container that also carries origins in <c>.env</c>.
    /// </summary>
    private GatewayConfigSnapshot Overlay(GatewayConfigSnapshot snapshot)
    {
        if (_environmentOrigins.Count == 0)
        {
            return snapshot;
        }

        var merged = GatewayCorsOptions.NormalizeOrigins(
            _environmentOrigins.Concat(snapshot.Cors.AllowedOrigins));

        return snapshot with
        {
            Cors = snapshot.Cors with { AllowedOrigins = merged },
        };
    }
}
