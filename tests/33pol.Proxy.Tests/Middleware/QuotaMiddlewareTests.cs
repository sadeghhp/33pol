using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

public sealed class QuotaMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_NonInferencePath_SkipsQuotaCheck()
    {
        var quota = Substitute.For<IQuotaService>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/health";

        var nextCalls = 0;
        var middleware = new QuotaMiddleware(
            _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            quota,
            new OpenAiErrorResponseWriter());

        await middleware.InvokeAsync(context);

        nextCalls.Should().Be(1);
        quota.DidNotReceive().CheckBeforeForward(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task InvokeAsync_QuotaExceeded_Returns429()
    {
        var quota = Substitute.For<IQuotaService>();
        quota.CheckBeforeForward("tenant-a", Arg.Any<string>())
            .Returns(QuotaCheckResult.HardExceeded);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Items[TenantContextKeys.HttpContextItemKey] = new TenantContext
        {
            TenantId = "tenant-a",
            ApiKeyId = Guid.NewGuid().ToString(),
            Role = ApiKeyRole.Inference,
        };

        var nextCalls = 0;
        var middleware = new QuotaMiddleware(
            _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            quota,
            new OpenAiErrorResponseWriter());

        await middleware.InvokeAsync(context);

        nextCalls.Should().Be(0);
        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("quota_exceeded");
    }

    [Fact]
    public async Task InvokeAsync_SoftWarning_SetsQuotaWarningHeader()
    {
        var quota = Substitute.For<IQuotaService>();
        quota.CheckBeforeForward(Arg.Any<string>(), Arg.Any<string>())
            .Returns(QuotaCheckResult.SoftWarning("approaching limit"));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";

        var middleware = new QuotaMiddleware(
            _ => Task.CompletedTask,
            quota,
            new OpenAiErrorResponseWriter());

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers[GatewayHeaders.QuotaWarning].ToString().Should().Be("approaching limit");
    }
}
