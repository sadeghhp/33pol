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
            Capabilities = [.. model.Capabilities],
            ModelType = model.ModelType,
        };
}
