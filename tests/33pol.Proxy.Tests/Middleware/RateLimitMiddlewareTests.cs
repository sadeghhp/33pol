using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Parsing;

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

    /// <summary>
    /// A body the router will reject anyway must not consume a request from the RPM bucket: with a
    /// budget of one request, a malformed request followed by a valid one must let the valid one
    /// through, and the malformed one gets the router's own error code rather than a 429.
    /// </summary>
    [Theory]
    [InlineData(true, "invalid_json")]
    [InlineData(false, "missing_model")]
    public async Task InvokeAsync_CachedUnroutableBody_RejectsWithoutDebitingRateLimit(
        bool invalidJson,
        string expectedErrorCode)
    {
        var resolver = new RateLimitPolicyResolver(new StubConfigProvider(new GatewayConfigSnapshot
        {
            RateLimits = new RateLimitsConfigSection { Default = new RateLimitPolicy(1, 0, 5) },
        }));
        var store = new InMemoryDistributedRateLimitStore();
        var nextCalls = 0;
        var middleware = new RateLimitMiddleware(
            _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            resolver,
            store,
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            TimeProvider.System);

        var rejected = CreateInferenceContext();
        if (invalidJson)
        {
            InferenceRequestParseCache.SetInvalidJson(rejected);
        }
        else
        {
            InferenceRequestParseCache.SetParsed(rejected, new InferenceRequestInfo(Model: null, Stream: false));
        }

        await middleware.InvokeAsync(rejected);

        nextCalls.Should().Be(0);
        rejected.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        rejected.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be(expectedErrorCode);

        var valid = CreateInferenceContext();
        InferenceRequestParseCache.SetParsed(valid, new InferenceRequestInfo(Model: "gpt", Stream: false));

        await middleware.InvokeAsync(valid);

        nextCalls.Should().Be(1, "the malformed request must not have consumed the only permitted request");
        valid.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>Without a cached parse the middleware behaves as before: it does not parse itself.</summary>
    [Fact]
    public async Task InvokeAsync_NoCachedParse_AcquiresAsBefore()
    {
        var resolver = new RateLimitPolicyResolver(new StubConfigProvider(new GatewayConfigSnapshot
        {
            RateLimits = new RateLimitsConfigSection { Default = new RateLimitPolicy(1, 0, 5) },
        }));
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

        await middleware.InvokeAsync(CreateInferenceContext());
        await middleware.InvokeAsync(CreateInferenceContext());

        nextCalls.Should().Be(1);
    }

    private static DefaultHttpContext CreateInferenceContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        return context;
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
