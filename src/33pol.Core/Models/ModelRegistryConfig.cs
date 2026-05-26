using System.Text.Json.Serialization;

namespace Pol33.Core.Models;

public sealed class ModelRegistryConfig
{
    [JsonPropertyName("models")]
    public List<ModelConfig>? Models { get; set; }
}
