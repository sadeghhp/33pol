using System.Text.Json;
using System.Text.Json.Nodes;
using Pol33.Core.Configuration;

namespace Pol33.Policy.Admin;

internal static class AppSettingsCorsPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    public static async Task WriteAsync(
        string appsettingsPath,
        IReadOnlyList<string> allowedOrigins,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(appsettingsPath)
            ?? throw new InvalidOperationException($"Cannot resolve directory for '{appsettingsPath}'.");

        Directory.CreateDirectory(directory);

        JsonObject root;
        if (File.Exists(appsettingsPath))
        {
            var existing = await File.ReadAllTextAsync(appsettingsPath, cancellationToken).ConfigureAwait(false);
            root = JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var gateway = root[GatewayOptions.SectionName] as JsonObject ?? new JsonObject();
        var cors = gateway[GatewayCorsOptions.SectionName] as JsonObject ?? new JsonObject();

        var originsArray = new JsonArray();
        foreach (var origin in allowedOrigins)
        {
            originsArray.Add(origin);
        }

        cors["AllowedOrigins"] = originsArray;
        gateway[GatewayCorsOptions.SectionName] = cors;
        root[GatewayOptions.SectionName] = gateway;

        var json = root.ToJsonString(JsonOptions);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(appsettingsPath)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

        try
        {
            File.Move(tempPath, appsettingsPath, overwrite: true);
        }
        catch (IOException)
        {
            await File.WriteAllTextAsync(appsettingsPath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
