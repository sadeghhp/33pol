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
    public async Task InvokeAsync_SoftWarning_RegistersOnStartingCallback()
    {
        var quota = Substitute.For<IQuotaService>();
        quota.CheckBeforeForward(Arg.Any<string>(), Arg.Any<string>())
            .Returns(QuotaCheckResult.SoftWarning("approaching limit"));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";

        var nextCalled = false;
        var middleware = new QuotaMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            quota,
            new OpenAiErrorResponseWriter());

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// Budget enforcement no longer runs here — it is done once, in ModelRouterMiddleware, by
    /// TryReserveAsync (see ModelRouterBudgetReservationTests and
    /// BillingBudgetEnforcementServiceTests). QuotaMiddleware is only responsible for token quotas.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_DoesNotPerformBudgetEnforcement()
    {
        var quota = Substitute.For<IQuotaService>();
        quota.CheckBeforeForward(Arg.Any<string>(), Arg.Any<string>()).Returns(QuotaCheckResult.Allowed);

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

        nextCalls.Should().Be(1);
        quota.Received(1).CheckBeforeForward("tenant-a", Arg.Any<string>());
    }
}
