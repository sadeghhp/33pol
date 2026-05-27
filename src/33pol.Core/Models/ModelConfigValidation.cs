using Pol33.Core.Providers;

namespace Pol33.Core.Models;

public static class ModelConfigValidation
{
    public static bool TryValidate(ModelConfig model, out string? error)
    {
        ArgumentNullException.ThrowIfNull(model);
        error = null;

        if (model.UpstreamAuth is null)
        {
            return true;
        }

        if (!string.Equals(model.UpstreamAuth.Type, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            error = "upstreamAuth.type must be 'bearer'.";
            return false;
        }

        return EnvVarNameValidator.TryValidate(model.UpstreamAuth.EnvVar, out _, out error);
    }
}
