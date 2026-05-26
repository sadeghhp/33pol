using Pol33.Core.Abstractions;

namespace Pol33.Proxy.Resilience;

public sealed class GatewayDrainState : IGatewayDrainState
{
    private int _isDraining;

    public bool IsDraining => Volatile.Read(ref _isDraining) != 0;

    public void BeginDrain() => Volatile.Write(ref _isDraining, 1);
}
