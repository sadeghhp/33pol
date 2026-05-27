using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IUpstreamBearerTokenResolver
{
    string? ResolveBearerToken(UpstreamAuthConfig? upstreamAuth);
}
