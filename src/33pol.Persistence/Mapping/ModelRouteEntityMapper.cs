using System.Text.Json;
using Pol33.Core.Models;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Mapping;

internal static class ModelRouteEntityMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ModelConfig ToModel(ModelRouteEntity entity) => new()
    {
        Id = entity.ModelId,
        Url = entity.Url,
        MaxContextLength = entity.MaxContextLength,
        // Deep-clone the lists so callers cannot mutate tracked entity state.
        Aliases = [.. entity.Aliases],
        Capabilities = [.. entity.Capabilities],
        PublicAccess = entity.PublicAccess,
        UpstreamAuth = string.IsNullOrWhiteSpace(entity.UpstreamAuthJson)
            ? null
            : JsonSerializer.Deserialize<UpstreamAuthConfig>(entity.UpstreamAuthJson, JsonOptions),
    };

    public static ModelRouteEntity ToEntity(ModelConfig model, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ModelId = model.Id,
        Url = model.Url,
        MaxContextLength = model.MaxContextLength,
        Aliases = [.. model.Aliases],
        Capabilities = [.. model.Capabilities],
        PublicAccess = model.PublicAccess,
        UpstreamAuthJson = model.UpstreamAuth is null
            ? null
            : JsonSerializer.Serialize(model.UpstreamAuth, JsonOptions),
        UpdatedAt = now,
    };
}
