namespace Pol33.Core.Models;

/// <summary>
/// The canonical model-type taxonomy. A model's type says what kind of work the upstream does,
/// which is what health checks and the admin UI dispatch on. It is deliberately separate from
/// <see cref="ModelConfig.Capabilities"/>: capabilities gate which gateway routes a model may serve
/// (an authorization concern), while the type describes the model itself.
/// </summary>
public static class ModelTypes
{
    public const string TextGeneration = "text-generation";

    public const string Embedding = "embedding";

    public const string Rerank = "rerank";

    public const string Ocr = "ocr";

    public const string ImageGeneration = "image-generation";

    public const string VideoGeneration = "video-generation";

    public const string AudioTranscription = "audio-transcription";

    /// <summary>Every recognised type, in the order the admin UI lists them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TextGeneration,
        Embedding,
        Rerank,
        Ocr,
        ImageGeneration,
        VideoGeneration,
        AudioTranscription,
    ];

    /// <summary>
    /// Spellings accepted on input and folded onto a canonical type, so operators can write
    /// "embeddings" or "text_generation" and configs written against other gateways still load.
    /// </summary>
    internal static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text-generation"] = TextGeneration,
        ["text_generation"] = TextGeneration,
        ["textgeneration"] = TextGeneration,
        ["text"] = TextGeneration,
        ["chat"] = TextGeneration,
        ["completion"] = TextGeneration,
        ["completions"] = TextGeneration,
        ["llm"] = TextGeneration,
        ["embedding"] = Embedding,
        ["embeddings"] = Embedding,
        ["embed"] = Embedding,
        ["rerank"] = Rerank,
        ["reranker"] = Rerank,
        ["reranking"] = Rerank,
        ["ocr"] = Ocr,
        ["vision-ocr"] = Ocr,
        ["image-generation"] = ImageGeneration,
        ["image_generation"] = ImageGeneration,
        ["image"] = ImageGeneration,
        ["text-to-image"] = ImageGeneration,
        ["video-generation"] = VideoGeneration,
        ["video_generation"] = VideoGeneration,
        ["video"] = VideoGeneration,
        ["text-to-video"] = VideoGeneration,
        ["audio-transcription"] = AudioTranscription,
        ["audio_transcription"] = AudioTranscription,
        ["transcription"] = AudioTranscription,
        ["speech-to-text"] = AudioTranscription,
    };

    /// <summary>
    /// Every alias the gateway accepts, grouped by the canonical type it folds onto.
    /// </summary>
    /// <remarks>
    /// Exposed so the admin UI can load the taxonomy from the server instead of keeping a hand-copied
    /// duplicate. The UI's copy had drifted to roughly a quarter of these aliases, so a model typed
    /// <c>vision-ocr</c> or <c>speech-to-text</c> displayed as text generation — and, worse, the edit
    /// dialog pre-selected that wrong value and silently rewrote the model on save.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AliasesByCanonicalType() =>
        All.ToDictionary(
            canonical => canonical,
            canonical => (IReadOnlyList<string>)Aliases
                .Where(pair => string.Equals(pair.Value, canonical, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .OrderBy(alias => alias, StringComparer.Ordinal)
                .ToList(),
            StringComparer.Ordinal);

    /// <summary>
    /// Folds <paramref name="modelType"/> onto a canonical type. Returns null for null/blank input
    /// and false for a value that is not recognised, so callers can reject it with a clear message.
    /// </summary>
    public static bool TryNormalize(string? modelType, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(modelType))
        {
            return true;
        }

        if (Aliases.TryGetValue(modelType.Trim(), out var canonical))
        {
            normalized = canonical;
            return true;
        }

        error = $"modelType '{modelType.Trim()}' is not recognised. Expected one of: {string.Join(", ", All)}.";
        return false;
    }

    /// <summary>Canonical form of <paramref name="modelType"/>, or null when blank or unrecognised.</summary>
    public static string? Normalize(string? modelType) =>
        TryNormalize(modelType, out var normalized, out _) ? normalized : null;

    public static bool IsKnown(string? modelType) => Normalize(modelType) is not null;

    /// <summary>
    /// The type to treat <paramref name="model"/> as. Prefers the explicit <c>modelType</c>; falls back
    /// to inferring from capabilities so models registered before the field existed still classify
    /// correctly, and finally to text generation (the pre-existing default behaviour).
    /// </summary>
    public static string Resolve(ModelConfig model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var explicitType = Normalize(model.ModelType);
        if (explicitType is not null)
        {
            return explicitType;
        }

        return InferFromCapabilities(model.Capabilities) ?? TextGeneration;
    }

    /// <summary>
    /// Infers a type from a capability list, for models registered before <c>modelType</c> existed.
    /// Only a single-purpose capability list is conclusive — a model that also serves chat is a
    /// text-generation model that happens to expose an extra route.
    /// </summary>
    public static string? InferFromCapabilities(IReadOnlyCollection<string>? capabilities)
    {
        // An empty list means "all routes" in ModelConfig, which tells us nothing about the type.
        if (capabilities is null || capabilities.Count == 0)
        {
            return null;
        }

        var canonical = capabilities
            .Select(Normalize)
            .Where(type => type is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (canonical.Count != 1)
        {
            return null;
        }

        return canonical[0];
    }
}
