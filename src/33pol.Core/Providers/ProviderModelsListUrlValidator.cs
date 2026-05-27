namespace Pol33.Core.Providers;

public static class ProviderModelsListUrlValidator
{
    public static bool TryValidate(string? modelsListUrl, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(modelsListUrl))
        {
            error = "modelsUrl is required for custom providers.";
            return false;
        }

        if (!Uri.TryCreate(modelsListUrl.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "modelsUrl must be an absolute URL.";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = "modelsUrl must use http or https.";
            return false;
        }

        if (parsed.IsLoopback)
        {
            error = "modelsUrl must not target loopback addresses.";
            return false;
        }

        uri = parsed;
        return true;
    }
}
