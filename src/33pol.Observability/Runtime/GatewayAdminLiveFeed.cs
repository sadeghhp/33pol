using Pol33.Core.Abstractions;

namespace Pol33.Observability.Runtime;

/// <summary>Exposes the runtime state's change counter to the admin live endpoint.</summary>
public sealed class GatewayAdminLiveFeed(GatewayRuntimeState runtimeState) : IAdminLiveFeed
{
    public long Version => runtimeState.Version;
}
