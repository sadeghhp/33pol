namespace Pol33.Core.Abstractions;

public interface IGatewayDrainState
{
    bool IsDraining { get; }

    void BeginDrain();
}
