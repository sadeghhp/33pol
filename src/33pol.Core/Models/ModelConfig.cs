using System.Text.Json.Serialization;

namespace Pol33.Core.Models;

public sealed class ModelConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("upstreamAuth")]
    public UpstreamAuthConfig? UpstreamAuth { get; set; }

    [JsonPropertyName("maxContextLength")]
    public int MaxContextLength { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = [];

    /// <summary>
    /// When true, inference may proceed without a valid 33pol API key (rate limits still apply).
    /// </summary>
    [JsonPropertyName("publicAccess")]
    public bool PublicAccess { get; set; }

    /// <summary>
    /// Supported inference capabilities (e.g. chat, completions, embeddings, rerank).
    /// Empty list means all routes are supported (backward-compatible default).
    /// </summary>
    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    public bool HasCapability(string capability) =>
        Capabilities.Count == 0 ||
        Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
}

public sealed class UpstreamAuthConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "bearer";

    [JsonPropertyName("envVar")]
    public string EnvVar { get; set; } = string.Empty;

    [JsonPropertyName("secretRef")]
    public string SecretRef { get; set; } = string.Empty;
}
