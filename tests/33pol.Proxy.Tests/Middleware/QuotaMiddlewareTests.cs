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
    private static IBudgetEnforcementService AllowedBudget()
    {
        var budget = Substitute.For<IBudgetEnforcementService>();
        budget.CheckBeforeForwardAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.Allowed);
        return budget;
    }

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
            AllowedBudget(),
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
            AllowedBudget(),
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
            AllowedBudget(),
            new OpenAiErrorResponseWriter());

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_BudgetHardStop_Returns429()
    {
        var quota = Substitute.For<IQuotaService>();
        var budget = Substitute.For<IBudgetEnforcementService>();
        budget.CheckBeforeForwardAsync("tenant-a", Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.HardExceeded("Monthly cap"));

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
            budget,
            new OpenAiErrorResponseWriter());

        await middleware.InvokeAsync(context);

        nextCalls.Should().Be(0);
        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        quota.DidNotReceive().CheckBeforeForward(Arg.Any<string>(), Arg.Any<string>());
    }
}
