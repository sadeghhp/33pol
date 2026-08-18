using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Security;

namespace Pol33.Api.Security;

/// <summary>
/// Answers "does this request come from an operator?" on routes the authentication handler treats
/// as anonymous (<c>/health</c>, <c>/metrics</c>), where an operator key is optional rather than
/// required and therefore never authenticated by the pipeline.
/// </summary>
/// <remarks>
/// On those routes <see cref="HttpContext.User"/> is always the empty principal, so evaluating the
/// Operator policy against it would deny every caller. Instead the credential is read from the
/// same headers the authentication handler honours, validated through <see cref="IApiKeyValidator"/>,
/// and projected onto a principal carrying the standard gateway claims. The policy is then
/// evaluated by <see cref="IAuthorizationService"/> exactly as it is everywhere else — including the
/// "authentication disabled" short-circuit, so a gateway with no keys behaves as it does on every
/// other route.
///
/// Scoped because <see cref="IApiKeyValidator"/> is.
/// </remarks>
public sealed class GatewayOperatorAccess(
    IApiKeyValidator validator,
    IAuthorizationService authorization)
{
    public async Task<bool> IsOperatorAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var principal = httpContext.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            var apiKey = ExtractApiKey(httpContext.Request);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var result = await validator.ValidateAsync(apiKey, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    principal = ToPrincipal(result);
                }
            }
        }

        var authorized = await authorization
            .AuthorizeAsync(principal, httpContext, GatewayAuthPolicies.Operator)
            .ConfigureAwait(false);
        return authorized.Succeeded;
    }

    /// <summary>
    /// Mirrors the header contract of the API-key authentication handler: <c>X-API-Key</c> first,
    /// then <c>Authorization: Bearer</c>.
    /// </summary>
    public static string? ExtractApiKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Headers.TryGetValue("X-API-Key", out var headerValue))
        {
            return headerValue.ToString();
        }

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        return null;
    }

    private static ClaimsPrincipal ToPrincipal(ApiKeyValidationResult result)
    {
        var claims = new List<Claim>
        {
            new(GatewayAuthClaims.TenantId, result.TenantId!.Value.ToString()),
            new(GatewayAuthClaims.ApiKeyId, result.ApiKeyId!.Value.ToString()),
            new(GatewayAuthClaims.Role, result.Role!.Value.ToString()),
        };

        if (!string.IsNullOrWhiteSpace(result.TenantSlug))
        {
            claims.Add(new Claim(GatewayAuthClaims.TenantSlug, result.TenantSlug));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, GatewayAuthSchemes.ApiKey));
    }
}
