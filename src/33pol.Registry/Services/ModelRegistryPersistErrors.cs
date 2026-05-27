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

        throw ex;
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
