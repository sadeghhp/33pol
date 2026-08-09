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

    [Fact]
    public async Task HandleAsync_OperatorPolicy_AuthDisabled_Succeeds()
    {
        var handler = CreateHandler(new GatewayAuthenticationState());
        var requirement = new GatewayAuthorizationRequirement(GatewayAuthPolicies.Operator);
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity()),
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
