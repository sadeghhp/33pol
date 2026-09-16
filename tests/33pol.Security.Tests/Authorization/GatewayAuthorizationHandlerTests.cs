using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Security;
using Pol33.Security.Authorization;
using Pol33.Security.Configuration;
using Pol33.Security.Hosting;

namespace Pol33.Security.Tests.Authorization;

public sealed class GatewayAuthorizationHandlerTests
{
    /// <summary>
    /// Anonymous inference is what the disabled mode is for, and it stays granted without a
    /// credential. Only the control-plane policies are refused.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AuthDisabled_SucceedsWithoutUser()
    {
        var handler = CreateHandler(new GatewayAuthenticationState());
        var requirement = new GatewayAuthorizationRequirement(GatewayAuthPolicies.Inference);
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity()),
            null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_AdminPolicy_InferenceKey_DoesNotSucceed()
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var handler = CreateHandler(authState);
        var requirement = new GatewayAuthorizationRequirement(GatewayAuthPolicies.Admin);
        var user = CreatePrincipal(ApiKeyRole.Inference);
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_InferencePolicy_AdminKey_DoesNotSucceed()
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var handler = CreateHandler(authState);
        var requirement = new GatewayAuthorizationRequirement(GatewayAuthPolicies.Inference);
        var user = CreatePrincipal(ApiKeyRole.Admin);
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    /// <summary>
    /// The operator policy is the admin role narrowed to the operator tenant. The role alone is
    /// per-tenant — any tenant's admin can mint further admin keys for its own tenant — so role-only
    /// gating handed every tenant's admin the gateway-wide control plane.
    /// </summary>
    [Theory]
    [InlineData("default", true)]
    [InlineData("DEFAULT", true)]
    [InlineData("tenant-b", false)]
    [InlineData(null, false)]
    public async Task HandleAsync_OperatorPolicy_RequiresOperatorTenantSlug(string? slug, bool expected)
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var handler = CreateHandler(authState, operatorSlug: "default");
        var requirement = new GatewayAuthorizationRequirement(GatewayAuthPolicies.Operator);
        var user = CreatePrincipal(ApiKeyRole.Admin, tenantSlug: slug);
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().Be(expected);
    }

    [Fact]
    public async Task HandleAsync_OperatorPolicy_InferenceKeyOfOperatorTenant_DoesNotSucceed()
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var handler = CreateHandler(authState, operatorSlug: "default");
        var requirement = new GatewayAuthorizationRequirement(GatewayAuthPolicies.Operator);
        var user = CreatePrincipal(ApiKeyRole.Inference, tenantSlug: "default");
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    /// <summary>
    /// The GW-01 regression, at the level that decides it. Disabling authentication used to succeed
    /// every policy, so a gateway with no key store handed its control plane to anyone who could
    /// reach the port. Admin and Operator are now refused in that mode — and because nothing can
    /// authenticate without a key store, refused for good. That is the accepted cost: the DB-less
    /// anonymous mode is an inference mode, not an administrative one.
    /// </summary>
    [Theory]
    [InlineData(GatewayAuthPolicies.Admin)]
    [InlineData(GatewayAuthPolicies.Operator)]
    public async Task HandleAsync_ControlPlanePolicy_AuthDisabled_IsDenied(string policyName)
    {
        var handler = CreateHandler(new GatewayAuthenticationState());
        var requirement = new GatewayAuthorizationRequirement(policyName);
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity()),
            null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    /// <summary>
    /// A real Admin key still authorizes the control plane while authentication is globally
    /// disabled — the mode denies anonymity, not credentials. Unreachable with the null validator in
    /// place, and pinned so the fix reads as "anonymous is refused", never "the policy is dead".
    /// </summary>
    [Fact]
    public async Task HandleAsync_AdminPolicy_AuthDisabled_StillAcceptsAnAdminKey()
    {
        var handler = CreateHandler(new GatewayAuthenticationState());
        var requirement = new GatewayAuthorizationRequirement(GatewayAuthPolicies.Admin);
        var context = new AuthorizationHandlerContext(
            [requirement],
            CreatePrincipal(ApiKeyRole.Admin),
            null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    private static GatewayAuthorizationHandler CreateHandler(
        GatewayAuthenticationState authState,
        string operatorSlug = "default") =>
        new(authState, new OperatorTenantConfiguration(operatorSlug));

    private static ClaimsPrincipal CreatePrincipal(ApiKeyRole role, string? tenantSlug = null)
    {
        var claims = new List<Claim> { new(GatewayAuthClaims.Role, role.ToString()) };
        if (tenantSlug is not null)
        {
            claims.Add(new Claim(GatewayAuthClaims.TenantSlug, tenantSlug));
        }

        var identity = new ClaimsIdentity(claims, GatewayAuthSchemes.ApiKey);
        return new ClaimsPrincipal(identity);
    }
}
