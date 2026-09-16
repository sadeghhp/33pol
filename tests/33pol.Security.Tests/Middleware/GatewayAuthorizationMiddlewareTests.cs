using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Security;
using Pol33.Security.Authentication;
using Pol33.Security.Authorization;
using Pol33.Security.Configuration;
using Pol33.Security.Hosting;
using Pol33.Security.Middleware;

namespace Pol33.Security.Tests.Middleware;

public sealed class GatewayAuthorizationMiddlewareTests
{
    /// <summary>
    /// Anonymous inference is the reason the disabled mode exists, and it survives the middleware no
    /// longer short-circuiting: the request is authorized for real and the production handler grants
    /// the Inference policy.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AuthDisabled_AllowsInferenceWithoutKey()
    {
        var authState = new GatewayAuthenticationState();
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var sut = new GatewayAuthorizationMiddleware(
            next, RealAuthorization(authState), new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/models";

        await sut.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    /// <summary>
    /// The GW-01 regression. With authentication globally disabled the middleware used to hand every
    /// request straight to the pipeline, so a gateway with no key store served its whole control
    /// plane to anyone who could reach the port. Authorization now runs in that mode too, and the
    /// production handler refuses Admin to an anonymous caller.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AuthDisabled_DeniesTheControlPlane()
    {
        var authState = new GatewayAuthenticationState();
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var sut = new GatewayAuthorizationMiddleware(
            next, RealAuthorization(authState), new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/api/rate-limits";
        context.Response.Body = new MemoryStream();

        await sut.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("invalid_api_key");
    }

    /// <summary>Probes and the scrape endpoint stay anonymous in the disabled mode as well.</summary>
    [Fact]
    public async Task InvokeAsync_AuthDisabled_StillAllowsAnonymousPaths()
    {
        var authState = new GatewayAuthenticationState();
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var sut = new GatewayAuthorizationMiddleware(
            next, RealAuthorization(authState), new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/ready";

        await sut.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_PublicHealthPath_SkipsAuthorization()
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var authorization = Substitute.For<IAuthorizationService>();
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var sut = new GatewayAuthorizationMiddleware(next, authorization, new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/live";

        await sut.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        await authorization.DidNotReceive().AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>());
    }

    [Fact]
    public async Task InvokeAsync_AdminPath_RequiresAdminPolicy()
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), GatewayAuthPolicies.Admin)
            .Returns(AuthorizationResult.Success());

        RequestDelegate next = _ => Task.CompletedTask;
        var sut = new GatewayAuthorizationMiddleware(next, authorization, new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/api/config/status";
        context.User = CreatePrincipal(ApiKeyRole.Admin);

        await sut.InvokeAsync(context);

        await authorization.Received(1).AuthorizeAsync(context.User, Arg.Any<object?>(), GatewayAuthPolicies.Admin);
    }

    [Fact]
    public async Task InvokeAsync_GetModelsListing_Unauthenticated_AllowsThrough()
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var authorization = Substitute.For<IAuthorizationService>();
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var sut = new GatewayAuthorizationMiddleware(next, authorization, new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/models";

        await sut.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        await authorization.DidNotReceive()
            .AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>());
    }

    [Fact]
    public async Task InvokeAsync_InferencePost_Unauthenticated_Returns401()
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), GatewayAuthPolicies.Inference)
            .Returns(AuthorizationResult.Failed());

        RequestDelegate next = _ => Task.CompletedTask;
        var sut = new GatewayAuthorizationMiddleware(next, authorization, new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Response.Body = new MemoryStream();

        await sut.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("invalid_api_key");
        context.Items[GatewayAuthContextItems.CredentialRejected].Should()
            .Be(true, "a refused credential is what the auth-failure limiter charges");
    }

    /// <summary>
    /// A recognised key without the role a route needs is answered 403 and left unmarked: it is not
    /// a guessed credential, so it must not spend the address's guessing budget.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AuthenticatedWithoutTheRole_Returns403WithoutMarking()
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), GatewayAuthPolicies.Admin)
            .Returns(AuthorizationResult.Failed());

        RequestDelegate next = _ => Task.CompletedTask;
        var sut = new GatewayAuthorizationMiddleware(next, authorization, new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/api/rate-limits";
        context.User = CreatePrincipal(ApiKeyRole.Inference);
        context.Response.Body = new MemoryStream();

        await sut.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("insufficient_scope");
        context.Items.ContainsKey(GatewayAuthContextItems.CredentialRejected).Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_PublicInference_Unauthenticated_AllowsThrough()
    {
        var authState = new GatewayAuthenticationState { IsAuthenticationRequired = true };
        var authorization = Substitute.For<IAuthorizationService>();
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var sut = new GatewayAuthorizationMiddleware(next, authorization, new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Items[PublicModelAccessKeys.IsPublicInference] = true;

        await sut.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    /// <summary>
    /// A real authorization service wired to the production handler and policies. The auth-disabled
    /// cases are exactly where a substituted <see cref="IAuthorizationService"/> would prove nothing:
    /// the whole question is what the real handler decides once the middleware stops short-circuiting.
    /// </summary>
    private static IAuthorizationService RealAuthorization(GatewayAuthenticationState authState)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGatewayAuthenticationState>(authState);
        services.AddSingleton(new OperatorTenantConfiguration("default"));
        services.AddSingleton<IAuthorizationHandler, GatewayAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            foreach (var policy in new[]
                     {
                         GatewayAuthPolicies.Inference,
                         GatewayAuthPolicies.Admin,
                         GatewayAuthPolicies.Operator,
                     })
            {
                options.AddPolicy(policy, builder =>
                    builder.AddRequirements(new GatewayAuthorizationRequirement(policy)));
            }
        });

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
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
