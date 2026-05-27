namespace Pol33.Api;

/// <summary>
/// Normalizes model ids from admin API route parameters. Encoded slashes (%2F) are not
/// decoded by the host router, so clients must percent-encode ids that contain '/'.
/// </summary>
public static class AdminModelRouteId
{
    public static string Decode(string routeId)
    {
        if (string.IsNullOrEmpty(routeId) || !routeId.Contains('%', StringComparison.Ordinal))
        {
            return routeId;
        }

        try
        {
            return Uri.UnescapeDataString(routeId);
        }
        catch (UriFormatException)
        {
            return routeId;
        }
    }
}
