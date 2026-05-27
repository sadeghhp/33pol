using Microsoft.Extensions.Configuration;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Registry.Services;

public sealed class UpstreamBearerTokenResolver(
    IUpstreamSecretStore secretStore,
    IConfiguration configuration) : IUpstreamBearerTokenResolver
{
    public string? ResolveBearerToken(UpstreamAuthConfig? upstreamAuth)
    {
        if (upstreamAuth is null)
        {
            return null;
        }

        if (!string.Equals(upstreamAuth.Type, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(upstreamAuth.SecretRef) &&
            UpstreamSecretRefs.TryParseModelId(upstreamAuth.SecretRef, out var modelId) &&
            secretStore.TryGet(modelId, out var secret))
        {
            return secret;
        }

        if (string.IsNullOrWhiteSpace(upstreamAuth.EnvVar))
        {
            return null;
        }

        var name = upstreamAuth.EnvVar.Trim();
        return configuration[name] ?? Environment.GetEnvironmentVariable(name);
    }
}
