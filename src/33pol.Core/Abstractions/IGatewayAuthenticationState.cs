namespace Pol33.Core.Abstractions;

public interface IGatewayAuthenticationState
{
    bool IsAuthenticationRequired { get; }
}
