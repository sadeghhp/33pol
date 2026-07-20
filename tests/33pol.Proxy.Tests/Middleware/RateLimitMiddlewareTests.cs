using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

public sealed class RateLimitMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenRpmExceeded_Returns429WithRetryAfter()
    {
        var resolver = new RateLimitPolicyResolver(new StubConfigProvider(new GatewayConfigSnapshot
        {
            RateLimits = new RateLimitsConfigSection { Default = new RateLimitPolicy(1, 0, 5) },
        }));
        var store = new InMemoryDistributedRateLimitStore();
        var errors = new OpenAiErrorResponseWriter();
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";

        var nextCalls = 0;
        var middleware = new RateLimitMiddleware(
            _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            resolver,
            store,
            errors,
            metrics,
            TimeProvider.System);

        await middleware.InvokeAsync(context);
        nextCalls.Should().Be(1);

        await middleware.InvokeAsync(context);

        nextCalls.Should().Be(1);
        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.Headers[GatewayHeaders.RetryAfter].ToString().Should().NotBeNullOrEmpty();
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("rate_limit_exceeded");
    }

    [Fact]
    public async Task InvokeAsync_WhenRateLimitingDisabled_NeverRejects()
    {
        // Same tier that rejects the second request above (rpm 1, burst 0), but with the master switch off.
        var resolver = new RateLimitPolicyResolver(new StubConfigProvider(new GatewayConfigSnapshot
        {
            RateLimits = new RateLimitsConfigSection
            {
                Enabled = false,
                Default = new RateLimitPolicy(1, 0, 5),
            },
        }));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";

        var nextCalls = 0;
        var middleware = new RateLimitMiddleware(
            _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            resolver,
            new InMemoryDistributedRateLimitStore(),
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            TimeProvider.System);

        for (var i = 0; i < 5; i++)
        {
            await middleware.InvokeAsync(context);
        }

        nextCalls.Should().Be(5);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public void IsEnabled_ReflectsSnapshotAndDefaultsToTrue()
    {
        var disabled = new RateLimitPolicyResolver(new StubConfigProvider(new GatewayConfigSnapshot
        {
            RateLimits = new RateLimitsConfigSection { Enabled = false },
        }));
        disabled.IsEnabled().Should().BeFalse();

        // A snapshot that predates the toggle (or a database-less deployment) must still enforce.
        var defaulted = new RateLimitPolicyResolver(new StubConfigProvider(new GatewayConfigSnapshot()));
        defaulted.IsEnabled().Should().BeTrue();
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }
}
