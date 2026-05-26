using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Security;
using Pol33.Security.Authorization;
using Pol33.Security.Hosting;

namespace Pol33.Security.Tests.Authorization;

public sealed class GatewayAuthorizationHandlerTests
{
    [Fact]
    public async Task HandleAsync_AuthDisabled_SucceedsWithoutUser()
    {
        var handler = new GatewayAuthorizationHandler(new GatewayAuthenticationState());
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
        var handler = new GatewayAuthorizationHandler(authState);
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
        var handler = new GatewayAuthorizationHandler(authState);
        var requirement = new GatewayAuthorizationRequirement(GatewayAuthPolicies.Inference);
        var user = CreatePrincipal(ApiKeyRole.Admin);
        var context = new AuthorizationHandlerContext([requirement], user, null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    private static ClaimsPrincipal CreatePrincipal(ApiKeyRole role)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(GatewayAuthClaims.Role, role.ToString()),
        ],
        GatewayAuthSchemes.ApiKey);

        return new ClaimsPrincipal(identity);
    }
}
