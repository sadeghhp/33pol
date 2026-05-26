using System.Text.Json.Serialization;

namespace Pol33.Core.Models;

public sealed class ModelConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("maxContextLength")]
    public int MaxContextLength { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = [];
}
