using System.Text.Json.Serialization;

namespace Pol33.Api.Models;

public sealed class OpenAiModelListResponse
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "list";

    [JsonPropertyName("data")]
    public IReadOnlyList<OpenAiModelResponse> Data { get; init; } = [];

    /// <summary>
    /// Short human-readable guidance, only present on anonymous listings when the gateway
    /// requires an API key. Tells the caller why some models are marked <c>requires_api_key</c>
    /// and how to authenticate.
    /// </summary>
    [JsonPropertyName("help")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Help { get; init; }
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

    /// <summary>
    /// Only present on anonymous responses when the gateway requires an API key: <c>true</c>
    /// when the caller must present an inference key to use this model, <c>false</c> when the
    /// model is open to the public. Omitted on authenticated responses.
    /// </summary>
    [JsonPropertyName("requires_api_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RequiresApiKey { get; init; }
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
