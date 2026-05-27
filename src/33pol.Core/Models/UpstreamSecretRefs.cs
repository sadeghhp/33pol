namespace Pol33.Core.Models;

public static class UpstreamSecretRefs
{
    public const string FileModelPrefix = "file:model:";

    public static string ForModel(string modelId) =>
        FileModelPrefix + modelId.Trim();

    public static bool TryParseModelId(string? secretRef, out string modelId)
    {
        modelId = string.Empty;
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            return false;
        }

        if (!secretRef.StartsWith(FileModelPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var id = secretRef[FileModelPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        modelId = id;
        return true;
    }

    public static bool IsValidForModel(string? secretRef, string modelId)
    {
        if (!TryParseModelId(secretRef, out var parsed))
        {
            return false;
        }

        return string.Equals(parsed, modelId.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
