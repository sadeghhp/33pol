using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Providers;

namespace Pol33.Registry.Services;

public sealed class UpstreamBearerTokenResolver(
    IUpstreamSecretStore secretStore,
    UpstreamEnvVarPolicy envVarPolicy,
    IConfiguration configuration,
    ILogger<UpstreamBearerTokenResolver> logger) : IUpstreamBearerTokenResolver
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

        // Enforced here, at the point of use, and not only on the admin write path. The admin API
        // applies this policy, but the file and database ingestion paths validate only that the name
        // is a syntactically valid identifier — and the identifier grammar happily accepts
        // "Gateway__Security__KeyPepper" and "ConnectionStrings__GatewayDb", which
        // Environment.GetEnvironmentVariable resolves. A model reaching the registry by any route
        // other than the admin API could therefore name one of the gateway's own secrets and have it
        // sent as a bearer token to a URL specified alongside it. Checking at resolve time closes
        // every path, present and future, in one place.
        if (!envVarPolicy.IsAllowed(name, out var policyError))
        {
            logger.LogError(
                "Refusing to resolve upstream credential from '{EnvVar}'. {PolicyError}", name, policyError);
            return null;
        }

        return configuration[name] ?? Environment.GetEnvironmentVariable(name);
    }
}
