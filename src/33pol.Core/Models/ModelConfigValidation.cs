using Pol33.Core.Providers;

namespace Pol33.Core.Models;

public static class ModelConfigValidation
{
    public static bool TryValidate(ModelConfig model, out string? error)
    {
        ArgumentNullException.ThrowIfNull(model);
        error = null;

        // The id is deliberately not checked here: an update body may leave it blank to keep the
        // existing id, and creation paths enforce it themselves.

        // Callers used to check only that url was non-blank, so "not a url" or "ftp://…" was
        // accepted into the registry and only failed per request in the forwarder (and threw in the
        // health checker's Uri constructor). Reject it at write time instead.
        if (string.IsNullOrWhiteSpace(model.Url) ||
            !Uri.TryCreate(model.Url.Trim(), UriKind.Absolute, out var url) ||
            (!string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            error = "url must be an absolute http or https URL.";
            return false;
        }

        if (!ModelTypes.TryNormalize(model.ModelType, out _, out error))
        {
            return false;
        }

        if (model.UpstreamAuth is null)
        {
            return true;
        }

        if (!string.Equals(model.UpstreamAuth.Type, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            error = "upstreamAuth.type must be 'bearer'.";
            return false;
        }

        var hasEnv = !string.IsNullOrWhiteSpace(model.UpstreamAuth.EnvVar);
        var hasRef = !string.IsNullOrWhiteSpace(model.UpstreamAuth.SecretRef);

        if (hasEnv && hasRef)
        {
            error = "upstreamAuth cannot set both envVar and secretRef.";
            return false;
        }

        if (!hasEnv && !hasRef)
        {
            error = "upstreamAuth requires envVar or secretRef.";
            return false;
        }

        if (hasRef)
        {
            if (!UpstreamSecretRefs.IsValidForModel(model.UpstreamAuth.SecretRef, model.Id))
            {
                error = "upstreamAuth.secretRef must be 'file:model:{modelId}' matching this model's id.";
                return false;
            }

            return true;
        }

        return EnvVarNameValidator.TryValidate(model.UpstreamAuth.EnvVar, out _, out error);
    }
}
