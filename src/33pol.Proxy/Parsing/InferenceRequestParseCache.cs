using Microsoft.AspNetCore.Http;

namespace Pol33.Proxy.Parsing;

/// <summary>
/// Carries the result of the single request-body parse from the first middleware that needs it to
/// every later one.
/// </summary>
/// <remarks>
/// <para>Both <c>PublicModelDetectionMiddleware</c> and <c>ModelRouterMiddleware</c> need the same
/// three routing scalars, and each used to parse the whole body for itself. That doubled the cost of
/// the most expensive step on the inference path, and the first of the two runs ahead of
/// authentication — so an unauthenticated caller paid for both a full buffer spill and a full parse
/// before any credential was checked.</para>
///
/// <para>A failed parse is cached too: the router must still answer <c>invalid_json</c>, and
/// re-parsing a body already known to be malformed only repeats the work.</para>
/// </remarks>
public static class InferenceRequestParseCache
{
    private const string ItemKey = "33pol.Inference.ParsedRequest";

    /// <summary>Records a successful parse of the body read from position 0.</summary>
    public static void SetParsed(HttpContext context, InferenceRequestInfo info)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[ItemKey] = new CachedParse(info);
    }

    /// <summary>Records that the body is not valid JSON, so no later stage retries the parse.</summary>
    public static void SetInvalidJson(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[ItemKey] = new CachedParse(null);
    }

    /// <summary>
    /// Returns the cached outcome, if any. <paramref name="info"/> is null when the cached outcome is
    /// "not valid JSON".
    /// </summary>
    public static bool TryGet(HttpContext context, out InferenceRequestInfo? info)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(ItemKey, out var cached) && cached is CachedParse parse)
        {
            info = parse.Info;
            return true;
        }

        info = null;
        return false;
    }

    private sealed record CachedParse(InferenceRequestInfo? Info);
}
