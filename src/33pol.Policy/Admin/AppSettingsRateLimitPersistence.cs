using System.Text.Json;
using System.Text.Json.Nodes;
using Pol33.Core.Configuration;

namespace Pol33.Policy.Admin;

internal static class AppSettingsRateLimitPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    public static async Task WriteAsync(
        string appsettingsPath,
        RateLimitTierOptions defaultTier,
        IReadOnlyDictionary<string, RateLimitTierOptions> plans,
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

        var rateLimiting = root[RateLimitingOptions.SectionName] as JsonObject ?? new JsonObject();
        var existingTenants = rateLimiting["Tenants"]?.DeepClone();
        var existingRetention = rateLimiting["InMemoryPartitionRetentionSeconds"]?.DeepClone();
        var existingCompaction = rateLimiting["InMemoryCompactionEveryOperations"]?.DeepClone();

        rateLimiting["Default"] = ToTierNode(defaultTier);
        rateLimiting["Plans"] = ToPlansNode(plans);

        if (existingTenants is not null)
        {
            rateLimiting["Tenants"] = existingTenants;
        }

        if (existingRetention is not null)
        {
            rateLimiting["InMemoryPartitionRetentionSeconds"] = existingRetention;
        }

        if (existingCompaction is not null)
        {
            rateLimiting["InMemoryCompactionEveryOperations"] = existingCompaction;
        }

        root[RateLimitingOptions.SectionName] = rateLimiting;

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

    private static JsonObject ToTierNode(RateLimitTierOptions tier) =>
        new()
        {
            ["Rpm"] = tier.Rpm,
            ["Burst"] = tier.Burst,
            ["MaxConcurrentStreams"] = tier.MaxConcurrentStreams,
        };

    private static JsonObject ToPlansNode(IReadOnlyDictionary<string, RateLimitTierOptions> plans)
    {
        var node = new JsonObject();
        foreach (var (slug, tier) in plans.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            node[slug] = ToTierNode(tier);
        }

        return node;
    }
}
