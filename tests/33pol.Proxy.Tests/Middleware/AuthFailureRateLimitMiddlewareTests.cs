using System.Net;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

public sealed class AuthFailureRateLimitMiddlewareTests
{
    /// <summary>
    /// The rate limiter proper sits behind authentication, so everything the security middleware
    /// refuses used to reach no limiter at all — a wrong key could be retried as fast as the network
    /// allowed.
    /// </summary>
    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/admin/api/keys")]
    public async Task InvokeAsync_RepeatedCredentialRejections_EventuallyAnswers429(string path)
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 2, Burst: 0, MaxConcurrentStreams: 0),
            _ => StatusCodes.Status401Unauthorized,
            out _);

        (await InvokeAsync(middleware, path)).Should().Be(StatusCodes.Status401Unauthorized);
        (await InvokeAsync(middleware, path)).Should().Be(StatusCodes.Status401Unauthorized);

        var refused = CreateContext(path);
        await middleware.InvokeAsync(refused);

        refused.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("rate_limit_exceeded");
        refused.Response.Headers[GatewayHeaders.RetryAfter].ToString().Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Only the outcome is charged. Traffic that authenticates is metered against its tenant by
    /// RateLimitMiddleware instead, so charging it here would bill it twice — and would let ordinary
    /// successful traffic exhaust the budget that exists to bound guessing.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_SuccessfulRequests_AreNeverCharged()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 2, Burst: 0, MaxConcurrentStreams: 0),
            _ => StatusCodes.Status200OK,
            out _);

        for (var i = 0; i < 50; i++)
        {
            (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status200OK);
        }
    }

    /// <summary>Each client address gets its own budget; one caller's guessing cannot lock out another.</summary>
    [Fact]
    public async Task InvokeAsync_PartitionsByClientAddress()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            _ => StatusCodes.Status401Unauthorized,
            out _);

        var noisy = IPAddress.Parse("203.0.113.7");
        (await InvokeAsync(middleware, "/v1/chat/completions", noisy)).Should().Be(StatusCodes.Status401Unauthorized);
        (await InvokeAsync(middleware, "/v1/chat/completions", noisy)).Should().Be(StatusCodes.Status429TooManyRequests);

        var innocent = IPAddress.Parse("203.0.113.8");
        (await InvokeAsync(middleware, "/v1/chat/completions", innocent)).Should().Be(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// The auth-failure budget and the tenant's own budget are separate stores of tokens: exhausting
    /// one must leave the other untouched.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ChargesAPartitionOfItsOwn_NotTheAnonymousOne()
    {
        var policy = new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0);
        var middleware = CreateMiddleware(policy, _ => StatusCodes.Status401Unauthorized, out var store);

        await InvokeAsync(middleware, "/v1/chat/completions");
        (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status429TooManyRequests);

        store.PeekRequest("anon:unknown", policy, DateTimeOffset.UtcNow).IsAcquired.Should()
            .BeTrue("the anonymous partition for the same address must be untouched");
    }

    /// <summary>Paths that carry no credential are not in scope, at any status.</summary>
    [Fact]
    public async Task InvokeAsync_NonCredentialPath_IsNeverCharged()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            _ => StatusCodes.Status401Unauthorized,
            out _);

        for (var i = 0; i < 20; i++)
        {
            (await InvokeAsync(middleware, "/v1/models")).Should().Be(StatusCodes.Status401Unauthorized);
        }
    }

    /// <summary>The gateway-wide master switch governs this budget like every other limit.</summary>
    [Fact]
    public async Task InvokeAsync_WhenRateLimitingDisabled_NeverRejects()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            _ => StatusCodes.Status401Unauthorized,
            out _,
            enabled: false);

        for (var i = 0; i < 20; i++)
        {
            (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<int> InvokeAsync(
        AuthFailureRateLimitMiddleware middleware,
        string path,
        IPAddress? remoteAddress = null)
    {
        var context = CreateContext(path, remoteAddress);
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    private static DefaultHttpContext CreateContext(string path, IPAddress? remoteAddress = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remoteAddress;
        return context;
    }

    private static AuthFailureRateLimitMiddleware CreateMiddleware(
        RateLimitPolicy policy,
        Func<HttpContext, int> downstreamStatus,
        out InMemoryDistributedRateLimitStore store,
        bool enabled = true)
    {
        var resolver = new RateLimitPolicyResolver(new StubConfigProvider(new GatewayConfigSnapshot
        {
            RateLimits = new RateLimitsConfigSection { Enabled = enabled, Default = policy },
        }));

        store = new InMemoryDistributedRateLimitStore();
        return new AuthFailureRateLimitMiddleware(
            context =>
            {
                context.Response.StatusCode = downstreamStatus(context);
                return Task.CompletedTask;
            },
            resolver,
            store,
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            TimeProvider.System);
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }
}
