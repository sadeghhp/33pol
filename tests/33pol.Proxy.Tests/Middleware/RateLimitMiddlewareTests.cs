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
        var resolver = new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot
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
            Substitute.For<IModelRegistry>(),
            timeProvider: TimeProvider.System);

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
        var resolver = new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot
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
            Substitute.For<IModelRegistry>(),
            timeProvider: TimeProvider.System);

        for (var i = 0; i < 5; i++)
        {
            await middleware.InvokeAsync(context);
        }

        nextCalls.Should().Be(5);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// A body the router will reject anyway is answered here rather than three middlewares later —
    /// but only after the bucket has been debited. Answering ahead of the debit made an unroutable
    /// body a free request, so a tenant could send malformed payloads without any ceiling at all.
    /// </summary>
    [Theory]
    [InlineData(true, "invalid_json")]
    [InlineData(false, "missing_model")]
    public async Task InvokeAsync_CachedUnroutableBody_RejectsAndStillDebitsRateLimit(
        bool invalidJson,
        string expectedErrorCode)
    {
        var resolver = new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot
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
            Substitute.For<IModelRegistry>(),
            timeProvider: TimeProvider.System);

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

        var next = CreateInferenceContext();
        InferenceRequestParseCache.SetParsed(next, new InferenceRequestInfo(Model: "gpt", Stream: false));

        await middleware.InvokeAsync(next);

        nextCalls.Should().Be(0, "the malformed request consumed the only permitted request");
        next.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        next.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("rate_limit_exceeded");
    }

    /// <summary>
    /// With the master switch off the early answer is skipped entirely — the router gives the same
    /// one a few frames later, and nothing here should run when rate limiting is not enforced.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_CachedUnroutableBody_WhenRateLimitingDisabled_FallsThroughToTheRouter()
    {
        var resolver = new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot
        {
            RateLimits = new RateLimitsConfigSection { Enabled = false, Default = new RateLimitPolicy(1, 0, 5) },
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
            Substitute.For<IModelRegistry>(),
            timeProvider: TimeProvider.System);

        var context = CreateInferenceContext();
        InferenceRequestParseCache.SetInvalidJson(context);

        await middleware.InvokeAsync(context);

        nextCalls.Should().Be(1);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// Every answer carries the partition's budget, so a client can pace itself rather than
    /// discovering the limit by being refused.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_PublishesBudgetHeaders_OnAdmissionAndOnRejection()
    {
        var resolver = new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot
        {
            RateLimits = new RateLimitsConfigSection { Default = new RateLimitPolicy(60, 2, 5) },
        }));
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = new RateLimitMiddleware(
            _ => Task.CompletedTask,
            resolver,
            store,
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            Substitute.For<IModelRegistry>(),
            timeProvider: TimeProvider.System);

        var admitted = CreateInferenceContext();
        await middleware.InvokeAsync(admitted);

        admitted.Response.Headers[GatewayHeaders.RateLimitLimit].ToString().Should().Be("62");
        admitted.Response.Headers[GatewayHeaders.RateLimitRemaining].ToString().Should().Be("61");
        admitted.Response.Headers[GatewayHeaders.RateLimitReset].ToString().Should().Be("1");

        // Drain the bucket, then confirm the refusal reports an empty one rather than no headers.
        for (var i = 0; i < 61; i++)
        {
            await middleware.InvokeAsync(CreateInferenceContext());
        }

        var refused = CreateInferenceContext();
        await middleware.InvokeAsync(refused);

        refused.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Response.Headers[GatewayHeaders.RateLimitLimit].ToString().Should().Be("62");
        refused.Response.Headers[GatewayHeaders.RateLimitRemaining].ToString().Should().Be("0");
    }

    /// <summary>Without a cached parse the middleware behaves as before: it does not parse itself.</summary>
    [Fact]
    public async Task InvokeAsync_NoCachedParse_AcquiresAsBefore()
    {
        var resolver = new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot
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
            Substitute.For<IModelRegistry>(),
            timeProvider: TimeProvider.System);

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
