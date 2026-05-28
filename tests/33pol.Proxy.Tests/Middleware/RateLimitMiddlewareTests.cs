using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

public sealed class RateLimitMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenRpmExceeded_Returns429WithRetryAfter()
    {
        var resolver = new RateLimitPolicyResolver(new FixedOptionsMonitor(new RateLimitingOptions
        {
            Default = new RateLimitTierOptions { Rpm = 1, Burst = 0, MaxConcurrentStreams = 5 },
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

    private sealed class FixedOptionsMonitor(RateLimitingOptions value) : IOptionsMonitor<RateLimitingOptions>
    {
        public RateLimitingOptions CurrentValue { get; } = value;

        public RateLimitingOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<RateLimitingOptions, string?> listener) => null;
    }
}
