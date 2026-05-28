using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Security;
using Pol33.Security.Authentication;
using Pol33.Security.Hosting;
using Pol33.Security.Middleware;

namespace Pol33.Security.Tests.Middleware;

public sealed class GatewayAuthorizationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_AuthDisabled_AllowsInferenceWithoutKey()
    {
        var authState = new GatewayAuthenticationState();
        var authorization = Substitute.For<IAuthorizationService>();
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var sut = new GatewayAuthorizationMiddleware(next, authState, authorization, new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/models";

        await sut.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        await authorization.DidNotReceive().AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>());
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

        var sut = new GatewayAuthorizationMiddleware(next, authState, authorization, new OpenAiErrorResponseWriter());
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
        var sut = new GatewayAuthorizationMiddleware(next, authState, authorization, new OpenAiErrorResponseWriter());
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

        var sut = new GatewayAuthorizationMiddleware(next, authState, authorization, new OpenAiErrorResponseWriter());
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
        var sut = new GatewayAuthorizationMiddleware(next, authState, authorization, new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Response.Body = new MemoryStream();

        await sut.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("invalid_api_key");
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

        var sut = new GatewayAuthorizationMiddleware(next, authState, authorization, new OpenAiErrorResponseWriter());
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Items[PublicModelAccessKeys.IsPublicInference] = true;

        await sut.InvokeAsync(context);

        nextCalled.Should().BeTrue();
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
