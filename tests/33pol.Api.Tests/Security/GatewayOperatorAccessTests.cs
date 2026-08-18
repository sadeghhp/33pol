using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Pol33.Api.Security;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.Security;

namespace Pol33.Api.Tests.Security;

/// <summary>
/// On the anonymous paths (/health, /metrics) the pipeline never authenticates, so an optional
/// operator credential has to be validated here and projected onto a principal before the Operator
/// policy is evaluated.
/// </summary>
public sealed class GatewayOperatorAccessTests
{
    [Fact]
    public async Task IsOperatorAsync_NoCredential_EvaluatesPolicyAgainstTheAnonymousPrincipal()
    {
        var validator = Substitute.For<IApiKeyValidator>();
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), GatewayAuthPolicies.Operator)
            .Returns(AuthorizationResult.Failed());
        var access = new GatewayOperatorAccess(validator, authorization);
        var context = new DefaultHttpContext();

        var result = await access.IsOperatorAsync(context, CancellationToken.None);

        result.Should().BeFalse();
        await validator.DidNotReceive().ValidateAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await authorization.Received(1).AuthorizeAsync(
            Arg.Is<ClaimsPrincipal>(p => p.Identity == null || !p.Identity.IsAuthenticated),
            context,
            GatewayAuthPolicies.Operator);
    }

    [Theory]
    [InlineData("X-API-Key")]
    [InlineData("Authorization")]
    public async Task IsOperatorAsync_ValidKey_BuildsPrincipalWithGatewayClaims(string header)
    {
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var validator = Substitute.For<IApiKeyValidator>();
        validator.ValidateAsync("sk-op", Arg.Any<CancellationToken>())
            .Returns(ApiKeyValidationResult.Success(tenantId, keyId, "default", null, null, ApiKeyRole.Admin));

        ClaimsPrincipal? seen = null;
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), GatewayAuthPolicies.Operator)
            .Returns(call =>
            {
                seen = call.ArgAt<ClaimsPrincipal>(0);
                return AuthorizationResult.Success();
            });

        var access = new GatewayOperatorAccess(validator, authorization);
        var context = new DefaultHttpContext();
        if (header == "X-API-Key")
        {
            context.Request.Headers["X-API-Key"] = "sk-op";
        }
        else
        {
            context.Request.Headers.Authorization = "Bearer sk-op";
        }

        var result = await access.IsOperatorAsync(context, CancellationToken.None);

        result.Should().BeTrue();
        seen.Should().NotBeNull();
        seen!.Identity!.IsAuthenticated.Should().BeTrue();
        seen.FindFirst(GatewayAuthClaims.TenantId)!.Value.Should().Be(tenantId.ToString());
        seen.FindFirst(GatewayAuthClaims.ApiKeyId)!.Value.Should().Be(keyId.ToString());
        seen.FindFirst(GatewayAuthClaims.Role)!.Value.Should().Be(nameof(ApiKeyRole.Admin));
        seen.FindFirst(GatewayAuthClaims.TenantSlug)!.Value.Should().Be("default");
    }

    [Fact]
    public async Task IsOperatorAsync_InvalidKey_FallsBackToTheAnonymousPrincipal()
    {
        var validator = Substitute.For<IApiKeyValidator>();
        validator.ValidateAsync("sk-bad", Arg.Any<CancellationToken>())
            .Returns(ApiKeyValidationResult.Fail(ApiKeyValidationFailure.Invalid));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), GatewayAuthPolicies.Operator)
            .Returns(AuthorizationResult.Failed());

        var access = new GatewayOperatorAccess(validator, authorization);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-API-Key"] = "sk-bad";

        (await access.IsOperatorAsync(context, CancellationToken.None)).Should().BeFalse();
        await authorization.Received(1).AuthorizeAsync(
            Arg.Is<ClaimsPrincipal>(p => p.Identity == null || !p.Identity.IsAuthenticated),
            context,
            GatewayAuthPolicies.Operator);
    }

    [Fact]
    public async Task IsOperatorAsync_AlreadyAuthenticated_UsesTheRequestPrincipalWithoutRevalidating()
    {
        var validator = Substitute.For<IApiKeyValidator>();
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), GatewayAuthPolicies.Operator)
            .Returns(AuthorizationResult.Success());

        var access = new GatewayOperatorAccess(validator, authorization);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(GatewayAuthClaims.Role, "Admin")], "test")),
        };
        context.Request.Headers["X-API-Key"] = "sk-whatever";

        (await access.IsOperatorAsync(context, CancellationToken.None)).Should().BeTrue();
        await validator.DidNotReceive().ValidateAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await authorization.Received(1).AuthorizeAsync(context.User, context, GatewayAuthPolicies.Operator);
    }
}
