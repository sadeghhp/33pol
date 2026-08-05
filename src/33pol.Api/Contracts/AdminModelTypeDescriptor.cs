using Pol33.Core.Models;

namespace Pol33.Api.Contracts;

/// <summary>
/// One canonical model type as the admin UI needs it: the value to store, a label to show, the
/// upstream endpoint its health check calls (null when no automated check exists), and every alias
/// the gateway will fold onto it.
/// </summary>
/// <remarks>
/// Served from the server so the UI has no second copy of the taxonomy to drift from.
/// </remarks>
public sealed record AdminModelTypeDescriptor(
    string Value,
    string Label,
    string? TestEndpoint,
    IReadOnlyList<string> Aliases)
{
    /// <summary>
    /// The upstream route each type's health probe uses. Kept beside the probe construction in
    /// AdminModelTestService, which is what actually issues these calls.
    /// </summary>
    private static string? TestEndpointFor(string modelType) => modelType switch
    {
        ModelTypes.TextGeneration => "/v1/chat/completions",
        // OCR models are served over the chat route as vision models.
        ModelTypes.Ocr => "/v1/chat/completions",
        ModelTypes.Embedding => "/v1/embeddings",
        ModelTypes.Rerank => "/v1/rerank",
        _ => null,
    };

    private static string LabelFor(string modelType) => modelType switch
    {
        ModelTypes.TextGeneration => "Text generation",
        ModelTypes.Embedding => "Embedding",
        ModelTypes.Rerank => "Rerank",
        ModelTypes.Ocr => "OCR",
        ModelTypes.ImageGeneration => "Image generation",
        ModelTypes.VideoGeneration => "Video generation",
        ModelTypes.AudioTranscription => "Audio transcription",
        _ => modelType,
    };

    public static IReadOnlyList<AdminModelTypeDescriptor> All()
    {
        var aliases = ModelTypes.AliasesByCanonicalType();

        return [.. ModelTypes.All.Select(type => new AdminModelTypeDescriptor(
            type,
            LabelFor(type),
            TestEndpointFor(type),
            aliases.TryGetValue(type, out var list) ? list : []))];
    }
}
