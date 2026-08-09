using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Security;
using Pol33.Security.Authentication;
using Pol33.Security.Configuration;

namespace Pol33.Security.Authorization;

public sealed class GatewayAuthorizationRequirement(string policyName) : IAuthorizationRequirement
{
    public string PolicyName { get; } = policyName;
}

public sealed class GatewayAuthorizationHandler : AuthorizationHandler<GatewayAuthorizationRequirement>
{
    private readonly IGatewayAuthenticationState _authState;
    private readonly OperatorTenantConfiguration _operatorTenant;

    public GatewayAuthorizationHandler(
        IGatewayAuthenticationState authState,
        OperatorTenantConfiguration operatorTenant)
    {
        _authState = authState;
        _operatorTenant = operatorTenant;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GatewayAuthorizationRequirement requirement)
    {
        if (!_authState.IsAuthenticationRequired)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.Resource is HttpContext httpContext &&
            requirement.PolicyName == GatewayAuthPolicies.Inference &&
            !PublicModelAccess.HasRejectedCredential(httpContext) &&
            (PublicModelAccess.IsPublicInferenceRequest(httpContext) ||
             PublicModelAccess.AllowsAnonymousModelsListing(httpContext)))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (!context.User.Identity?.IsAuthenticated ?? true)
        {
            return Task.CompletedTask;
        }

        var roleClaim = context.User.FindFirst(GatewayAuthClaims.Role)?.Value;
        if (!Enum.TryParse<ApiKeyRole>(roleClaim, out var role))
        {
            return Task.CompletedTask;
        }

        var allowed = requirement.PolicyName switch
        {
            GatewayAuthPolicies.Admin => role is ApiKeyRole.Admin or ApiKeyRole.Both,
            // Role alone is per-tenant; the gateway-wide control plane additionally requires the key
            // to belong to the operator tenant. Without the second check, any tenant's admin could
            // rewrite model routes, upstream secrets, global rate limits, and read every tenant's
            // recent requests.
            GatewayAuthPolicies.Operator => (role is ApiKeyRole.Admin or ApiKeyRole.Both) &&
                _operatorTenant.IsOperatorTenant(
                    context.User.FindFirst(GatewayAuthClaims.TenantSlug)?.Value),
            GatewayAuthPolicies.Inference => role is ApiKeyRole.Inference or ApiKeyRole.Both,
            _ => false,
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
