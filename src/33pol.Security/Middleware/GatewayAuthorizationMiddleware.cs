using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Security;
using Pol33.Security.Authentication;
using Pol33.Security.Errors;

namespace Pol33.Security.Middleware;

public sealed class GatewayAuthorizationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IGatewayAuthenticationState _authState;
    private readonly IAuthorizationService _authorization;
    private readonly IErrorResponseWriter _errors;

    public GatewayAuthorizationMiddleware(
        RequestDelegate next,
        IGatewayAuthenticationState authState,
        IAuthorizationService authorization,
        IErrorResponseWriter errors)
    {
        _next = next;
        _authState = authState;
        _authorization = authorization;
        _errors = errors;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_authState.IsAuthenticationRequired || PublicGatewayPaths.IsAnonymous(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        var policy = path.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase)
            ? GatewayAuthPolicies.Admin
            : RequiresInferencePolicy(path)
                ? GatewayAuthPolicies.Inference
                : null;

        if (policy is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (policy == GatewayAuthPolicies.Inference &&
            (PublicModelAccess.IsPublicInferenceRequest(context) ||
             PublicModelAccess.AllowsAnonymousModelsListing(context)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var result = await _authorization.AuthorizeAsync(context.User, policy).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.InvalidApiKey),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await context.WriteGatewayErrorAsync(
            _errors.Write(GatewayErrorCode.InsufficientScope),
            context.RequestAborted).ConfigureAwait(false);
    }

    private static bool RequiresInferencePolicy(string path) =>
        path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);
}
