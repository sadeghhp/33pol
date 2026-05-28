using System.Text.Json;
using Pol33.Core.Models;

namespace Pol33.Registry.Services;

internal static class ModelRegistryPersistence
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    internal static ModelRegistryConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<ModelRegistryConfig>(json, JsonOptions)
        ?? throw new JsonException("Model registry configuration deserialized to null.");

    internal static (Dictionary<string, ModelConfig> Lookup, List<ModelConfig> Models) BuildLookup(
        IReadOnlyList<ModelConfig> source)
    {
        if (source.Count == 0)
        {
            throw new InvalidOperationException("Cannot build registry lookup from an empty model list.");
        }

        var lookup = new Dictionary<string, ModelConfig>(StringComparer.OrdinalIgnoreCase);
        var models = new List<ModelConfig>(source.Count);

        foreach (var model in source)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
            {
                throw new JsonException("Model entry is missing required 'id'.");
            }

            if (string.IsNullOrWhiteSpace(model.Url))
            {
                throw new JsonException($"Model '{model.Id}' is missing required 'url'.");
            }

            if (models.Any(m => string.Equals(m.Id, model.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new JsonException($"Duplicate model id '{model.Id}'.");
            }

            models.Add(CloneModel(model));
            var canonical = models[^1];
            lookup[canonical.Id] = canonical;

            foreach (var alias in model.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                if (lookup.TryGetValue(alias, out var existingAlias) && !ReferenceEquals(existingAlias, canonical))
                {
                    throw new JsonException($"Duplicate alias '{alias}' in registry.");
                }

                lookup[alias] = canonical;
            }
        }

        return (lookup, models);
    }

    internal static ModelConfig CloneModel(ModelConfig model) =>
        new()
        {
            Id = model.Id,
            Url = model.Url,
            UpstreamAuth = model.UpstreamAuth is null
                ? null
                : new UpstreamAuthConfig
                {
                    Type = model.UpstreamAuth.Type,
                    EnvVar = model.UpstreamAuth.EnvVar,
                    SecretRef = model.UpstreamAuth.SecretRef,
                },
            MaxContextLength = model.MaxContextLength,
            Aliases = [.. model.Aliases],
            PublicAccess = model.PublicAccess,
        };

    internal static async Task WriteAtomicAsync(
        string configPath,
        IReadOnlyList<ModelConfig> models,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException($"Cannot resolve directory for '{configPath}'.");

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(configPath)}.{Guid.NewGuid():N}.tmp");
        var payload = new ModelRegistryConfig { Models = models.Select(CloneModel).ToList() };
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

        try
        {
            File.Move(tempPath, configPath, overwrite: true);
        }
        catch (IOException)
        {
            // Single-file Docker bind mounts often reject rename-over-target (EBUSY).
            // Same-directory rename usually works; if not, overwrite in place.
            await File.WriteAllTextAsync(configPath, json, cancellationToken).ConfigureAwait(false);
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
