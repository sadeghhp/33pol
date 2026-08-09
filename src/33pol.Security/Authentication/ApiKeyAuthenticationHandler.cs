using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Security;
using Pol33.Core.Errors;
using Pol33.Security.Errors;
using Pol33.Security.Identity;

namespace Pol33.Security.Authentication;

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IApiKeyValidator _validator;
    private readonly IGatewayAuthenticationState _authState;
    private readonly IErrorResponseWriter _errors;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyValidator validator,
        IGatewayAuthenticationState authState,
        IErrorResponseWriter errors)
        : base(options, logger, encoder)
    {
        _validator = validator;
        _authState = authState;
        _errors = errors;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (PublicGatewayPaths.IsAnonymous(Request.Path))
        {
            return AuthenticateResult.NoResult();
        }

        var isPublicInference = PublicModelAccess.IsPublicInferenceRequest(Context);
        var allowsAnonymousModelsListing = PublicModelAccess.AllowsAnonymousModelsListing(Context);

        var apiKey = ExtractApiKey(Request);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (isPublicInference || allowsAnonymousModelsListing)
            {
                return AuthenticateResult.NoResult();
            }

            return _authState.IsAuthenticationRequired
                ? AuthenticateResult.Fail("missing_api_key")
                : AuthenticateResult.NoResult();
        }

        var result = await _validator.ValidateAsync(apiKey, Context.RequestAborted).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            if (!_authState.IsAuthenticationRequired)
            {
                return AuthenticateResult.NoResult();
            }

            var failure = result.Failure ?? ApiKeyValidationFailure.Invalid;

            // On the anonymous-capable routes an unrecognised key is treated as no key at all.
            // OpenAI-compatible SDKs refuse to construct a client with an empty api_key, so nearly
            // every caller of a public model sends a placeholder ("lm-studio", "not-needed", ...);
            // rejecting those made publicAccess unusable from the very clients it exists to serve,
            // and it costs nothing to ignore them because the route grants anonymous callers the
            // same access anyway.
            //
            // A key the gateway does recognise is the opposite case and still fails loudly here:
            // answering 200 to a revoked, expired, or deactivated-tenant key would leave its holder
            // believing the credential still works, with no signal anywhere that it had stopped.
            if ((isPublicInference || allowsAnonymousModelsListing) && !failure.IsRecognizedCredential())
            {
                return AuthenticateResult.NoResult();
            }

            var code = failure switch
            {
                ApiKeyValidationFailure.Expired => "expired_api_key",
                _ => "invalid_api_key",
            };

            Context.Items[GatewayAuthContextItems.AuthFailureCode] = code;
            return AuthenticateResult.Fail(code);
        }

        return Success(result);
    }

    private AuthenticateResult Success(ApiKeyValidationResult result)
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

        var identity = new ClaimsIdentity(claims, GatewayAuthSchemes.ApiKey);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, GatewayAuthSchemes.ApiKey);

        Context.SetTenantContext(new TenantContext
        {
            TenantId = result.TenantId!.Value.ToString(),
            ApiKeyId = result.ApiKeyId!.Value.ToString(),
            TenantSlug = result.TenantSlug,
            PlanSlug = result.PlanSlug,
            CostCenter = result.CostCenter,
            Role = result.Role!.Value,
        });

        return AuthenticateResult.Success(ticket);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!_authState.IsAuthenticationRequired || PublicGatewayPaths.IsAnonymous(Request.Path))
        {
            return;
        }

        var errorCode = Context.Items.TryGetValue(GatewayAuthContextItems.AuthFailureCode, out var value) &&
                        value?.ToString() == "expired_api_key"
            ? GatewayErrorCode.ExpiredApiKey
            : GatewayErrorCode.InvalidApiKey;

        await Context.WriteGatewayErrorAsync(
            _errors.Write(errorCode),
            Context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Authenticated but not authorized (e.g. a tenant admin on an operator-only route). The default
    /// forbid is an empty 403; clients of this gateway expect the OpenAI-shaped error body.
    /// </summary>
    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        await Context.WriteGatewayErrorAsync(
            _errors.Write(GatewayErrorCode.InsufficientScope),
            Context.RequestAborted).ConfigureAwait(false);
    }

    private static string? ExtractApiKey(HttpRequest request)
    {
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
}
