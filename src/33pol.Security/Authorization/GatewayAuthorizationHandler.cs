using Microsoft.AspNetCore.Authorization;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Security;

namespace Pol33.Security.Authorization;

public sealed class GatewayAuthorizationRequirement(string policyName) : IAuthorizationRequirement
{
    public string PolicyName { get; } = policyName;
}

public sealed class GatewayAuthorizationHandler : AuthorizationHandler<GatewayAuthorizationRequirement>
{
    private readonly IGatewayAuthenticationState _authState;

    public GatewayAuthorizationHandler(IGatewayAuthenticationState authState) => _authState = authState;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GatewayAuthorizationRequirement requirement)
    {
        if (!_authState.IsAuthenticationRequired)
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
