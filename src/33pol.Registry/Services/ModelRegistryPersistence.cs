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

    public static async Task<ModelRegistryConfig> ReadAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return new ModelRegistryConfig { Models = [] };
        }

        var json = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ModelRegistryConfig>(json, JsonOptions)
            ?? throw new JsonException("Model registry configuration deserialized to null.");
    }

    public static async Task WriteAtomicAsync(
        string configPath,
        ModelRegistryConfig config,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(configPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public static void ValidateModel(ModelConfig model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(model.Id))
        {
            throw new ArgumentException("Model entry is missing required 'id'.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(model.Url))
        {
            throw new ArgumentException($"Model '{model.Id}' is missing required 'url'.", nameof(model));
        }
    }

    public static List<ModelConfig> CloneModels(IEnumerable<ModelConfig> models) =>
        models.Select(m => new ModelConfig
        {
            Id = m.Id,
            Url = m.Url,
            MaxContextLength = m.MaxContextLength,
            Aliases = [.. m.Aliases],
        }).ToList();
}
