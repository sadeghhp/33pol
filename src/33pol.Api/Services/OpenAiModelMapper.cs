using Pol33.Api.Models;
using Pol33.Core.Models;

namespace Pol33.Api.Services;

internal static class OpenAiModelMapper
{
    internal const long DefaultCreatedEpoch = 1_733_328_000;

    public static OpenAiModelResponse ToResponse(ModelConfig model, bool available) =>
        new()
        {
            Id = model.Id,
            Created = DefaultCreatedEpoch,
            Permission = [CreateSyntheticPermission(model.Id)],
            Root = model.Id,
            Parent = null,
            MaxModelLen = model.MaxContextLength,
            Available = available,
        };

    private static OpenAiModelPermission CreateSyntheticPermission(string modelId) =>
        new()
        {
            Id = $"modelperm-{modelId}",
            Created = DefaultCreatedEpoch,
            AllowCreateEngine = false,
            AllowSampling = true,
            AllowLogprobs = true,
            AllowSearchIndices = false,
            AllowView = true,
            AllowFineTuning = false,
            Organization = "*",
            Group = null,
            IsBlocking = false,
        };
}
