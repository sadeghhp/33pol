using System.Text.Json.Serialization;

namespace Pol33.Api.Models;

public sealed class OpenAiModelListResponse
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "list";

    [JsonPropertyName("data")]
    public IReadOnlyList<OpenAiModelResponse> Data { get; init; } = [];

    /// <summary>
    /// Only present on anonymous listings when the gateway requires an API key: a minimal
    /// inventory of every healthy model and whether a key is needed to use it, so a caller
    /// with an empty <c>data</c> can see what the gateway offers and that they must get a key.
    /// Omitted on authenticated responses.
    /// </summary>
    [JsonPropertyName("models")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModelAvailabilityHint>? Models { get; init; }
}

public sealed class ModelAvailabilityHint
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("api_key_required")]
    public required bool ApiKeyRequired { get; init; }
}

public sealed class OpenAiModelResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "model";

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; init; } = "llm-gateway";

    [JsonPropertyName("permission")]
    public IReadOnlyList<OpenAiModelPermission> Permission { get; init; } = [];

    [JsonPropertyName("root")]
    public required string Root { get; init; }

    [JsonPropertyName("parent")]
    public string? Parent { get; init; }

    [JsonPropertyName("max_model_len")]
    public int MaxModelLen { get; init; }

    [JsonPropertyName("available")]
    public bool Available { get; init; }
}

public sealed class OpenAiModelPermission
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "model_permission";

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("allow_create_engine")]
    public bool AllowCreateEngine { get; init; }

    [JsonPropertyName("allow_sampling")]
    public bool AllowSampling { get; init; } = true;

    [JsonPropertyName("allow_logprobs")]
    public bool AllowLogprobs { get; init; } = true;

    [JsonPropertyName("allow_search_indices")]
    public bool AllowSearchIndices { get; init; }

    [JsonPropertyName("allow_view")]
    public bool AllowView { get; init; } = true;

    [JsonPropertyName("allow_fine_tuning")]
    public bool AllowFineTuning { get; init; }

    [JsonPropertyName("organization")]
    public string Organization { get; init; } = "*";

    [JsonPropertyName("group")]
    public string? Group { get; init; }

    [JsonPropertyName("is_blocking")]
    public bool IsBlocking { get; init; }
}
