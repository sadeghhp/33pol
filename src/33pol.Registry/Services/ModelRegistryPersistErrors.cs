using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pol33.Core.Models;

namespace Pol33.Registry.Services;

internal static class ModelRegistryPersistErrors
{
    internal static RegistryMutationResult FromException(Exception ex, string configPath, ILogger logger, string operation)
    {
        if (ex is JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Registry validation failed during {Operation}.", operation);
            return RegistryMutationResult.Fail(ex.Message);
        }

        if (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Registry persist failed during {Operation}.", operation);
            return RegistryMutationResult.Fail(FormatIOException(ex, configPath), suggestedStatusCode: 503);
        }

        // Anything else (a database error, a full disk) used to be rethrown, which surfaced as an
        // unhandled 500 with no gateway error body and no hint about what the operator should do.
        // The mutation left nothing behind — validation and persistence both happen before the
        // registry is swapped — so report it as a retryable service failure.
        logger.LogError(ex, "Registry persist failed unexpectedly during {Operation}.", operation);
        return RegistryMutationResult.Fail(
            $"Could not save the model registry during {operation}: {ex.Message}",
            suggestedStatusCode: 503);
    }

    internal static string FormatIOException(Exception ex, string configPath)
    {
        var detail = ex.Message;
        var readOnlyOrBusy =
            detail.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("read-only", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("ReadOnly", StringComparison.OrdinalIgnoreCase) ||
            ex is UnauthorizedAccessException;

        if (readOnlyOrBusy)
        {
            return
                $"Cannot save model registry to '{configPath}'. The file may be read-only or locked "
                + "(common with Docker :ro volume mounts). Use a writable mount, or edit "
                + "deploy/docker/config/models.json on the host and use Admin → Reload config.";
        }

        return $"Cannot save model registry to '{configPath}': {detail}";
    }
}
