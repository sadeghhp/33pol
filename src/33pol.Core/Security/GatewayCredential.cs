namespace Pol33.Core.Security;

/// <summary>
/// Where a gateway credential lives on a request, and how to read it out.
/// </summary>
/// <remarks>
/// <para>Defined over plain header values rather than an <c>HttpContext</c> so it can live in Core:
/// the security layer reads the credential to authenticate it, and the proxy layer needs the much
/// weaker question "was one presented at all" before authentication has run. Two implementations of
/// that question would eventually disagree, and the symptom would be a limiter charging — or
/// exempting — the wrong requests.</para>
/// </remarks>
public static class GatewayCredential
{
    /// <summary>The gateway's own credential header, checked before <c>Authorization</c>.</summary>
    public const string ApiKeyHeader = "X-API-Key";

    /// <summary>The scheme prefix accepted on <c>Authorization</c>, for OpenAI-compatible clients.</summary>
    public const string BearerPrefix = "Bearer ";

    /// <summary>
    /// The credential carried by a request, or null when it carries none.
    /// </summary>
    /// <param name="apiKeyHeader">The <see cref="ApiKeyHeader"/> value, if any.</param>
    /// <param name="authorizationHeader">The <c>Authorization</c> value, if any.</param>
    /// <remarks>
    /// A present-but-blank <see cref="ApiKeyHeader"/> (some proxies and SDKs always send the header)
    /// must not shadow a valid bearer token on the same request, so it is skipped rather than
    /// returned as an empty credential.
    /// </remarks>
    public static string? Extract(string? apiKeyHeader, string? authorizationHeader)
    {
        if (!string.IsNullOrWhiteSpace(apiKeyHeader))
        {
            return apiKeyHeader;
        }

        if (authorizationHeader is not null &&
            authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = authorizationHeader[BearerPrefix.Length..].Trim();
            return token.Length == 0 ? null : token;
        }

        return null;
    }

    /// <summary>
    /// Whether the request presents a credential at all — not whether it is a valid one.
    /// </summary>
    /// <remarks>
    /// The distinction the auth-failure limiter turns on: a caller offering no credential is not
    /// guessing at one, so it must be neither charged against that budget nor refused by it.
    /// </remarks>
    public static bool IsPresent(string? apiKeyHeader, string? authorizationHeader) =>
        Extract(apiKeyHeader, authorizationHeader) is not null;
}
