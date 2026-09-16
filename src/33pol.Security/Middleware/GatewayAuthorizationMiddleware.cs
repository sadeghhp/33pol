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
    private readonly IAuthorizationService _authorization;
    private readonly IErrorResponseWriter _errors;

    // No IGatewayAuthenticationState. It used to be read for one thing only — skipping the whole
    // check when authentication was globally disabled — and a security middleware holding an
    // auth-state it never consults is an invitation to put that bypass back.
    public GatewayAuthorizationMiddleware(
        RequestDelegate next,
        IAuthorizationService authorization,
        IErrorResponseWriter errors)
    {
        _next = next;
        _authorization = authorization;
        _errors = errors;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only genuinely anonymous paths skip the check. "Authentication is globally disabled" used
        // to skip it too, which meant the one mode with no key store was also the one mode where
        // /admin/api was never authorized at all. The Inference policy still succeeds anonymously in
        // that mode — the handler grants it — so public inference is unaffected; the difference is
        // that the control plane now goes through authorization like everything else.
        if (PublicGatewayPaths.IsAnonymous(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var policy = context.Request.Path.StartsWithSegments("/admin/api", StringComparison.OrdinalIgnoreCase)
            ? GatewayAuthPolicies.Admin
            : RequiresInferencePolicy(context.Request.Path)
                ? GatewayAuthPolicies.Inference
                : null;

        if (policy is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (policy == GatewayAuthPolicies.Inference &&
            !PublicModelAccess.HasRejectedCredential(context) &&
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
            // No usable credential reached this point: the outcome the auth-failure limiter charges.
            context.Items[GatewayAuthContextItems.CredentialRejected] = true;
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.InvalidApiKey),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // A recognised key without the role the route needs. Not a credential rejection — there is
        // nothing being guessed — so it is left unmarked and never spends the guessing budget.
        await context.WriteGatewayErrorAsync(
            _errors.Write(GatewayErrorCode.InsufficientScope),
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Segment-anchored, so a path cannot slip past policy selection while still being routable.
    /// </summary>
    private static bool RequiresInferencePolicy(PathString path) =>
        path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase);
}
