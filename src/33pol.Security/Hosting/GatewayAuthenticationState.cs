using Pol33.Core.Abstractions;

namespace Pol33.Security.Hosting;

public sealed class GatewayAuthenticationState : IGatewayAuthenticationState
{
    public bool IsAuthenticationRequired { get; set; }
}
