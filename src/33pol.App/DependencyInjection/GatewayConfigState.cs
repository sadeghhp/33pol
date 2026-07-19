using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.App.DependencyInjection;

/// <summary>
/// Holds the current configuration snapshot behind a lock-free volatile reference. The syncer swaps
/// in a new immutable snapshot with <see cref="Set"/>; the hot path reads <see cref="Current"/>
/// without locking and never sees a torn update. Starts at
/// <see cref="GatewayConfigSnapshot.Defaults"/> so reads are safe before the first database load.
/// </summary>
internal sealed class GatewayConfigState : IGatewayConfigProvider
{
    private volatile GatewayConfigSnapshot _current;

    public GatewayConfigState(GatewayConfigSnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public GatewayConfigSnapshot Current => _current;

    public void Set(GatewayConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _current = snapshot;
    }
}
