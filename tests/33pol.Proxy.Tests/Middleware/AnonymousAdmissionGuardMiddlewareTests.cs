using System.Net;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// The pre-parse gate. Public-model detection has to run before authentication, and it buffers and
/// JSON-parses the body to do its job — so without this, an anonymous flood was parsed in full
/// before any limiter had looked at it.
/// </summary>
public sealed class AnonymousAdmissionGuardMiddlewareTests
{
    private static readonly RateLimitPolicy Anonymous = new(Rpm: 60, Burst: 0, MaxConcurrentStreams: 0);

    [Fact]
    public async Task InvokeAsync_AnonymousCallerOverBudget_IsRefusedWithoutReachingTheParse()
    {
        var middleware = Create(out var store, out var reached);
        Drain(store, "anon:203.0.113.7");

        var context = CreateContext(remoteAddress: IPAddress.Parse("203.0.113.7"));
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        reached.Value.Should().BeFalse("nothing downstream may run, least of all the body parse");
    }

    /// <summary>
    /// It peeks; the token is taken by RateLimitMiddleware against the same partition a few frames
    /// later. Charging here as well would bill an anonymous caller twice for one request.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WithinBudget_PassesThroughAndSpendsNothing()
    {
        var middleware = Create(out var store, out var reached);

        for (var i = 0; i < 40; i++)
        {
            var context = CreateContext(remoteAddress: IPAddress.Parse("203.0.113.7"));
            await middleware.InvokeAsync(context);
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        reached.Value.Should().BeTrue();
        store.PeekRequest(RateLimitKeys.Tenant("anon:203.0.113.7"), Anonymous, DateTimeOffset.UtcNow)
            .Remaining.Should().Be(Anonymous.Capacity, "the guard never debits");
    }

    /// <summary>
    /// A credentialed request's tier depends on the tenant its key resolves to, which nothing knows
    /// this early. Refusing it against the address's anonymous bucket would throttle a paying tenant
    /// for the traffic of whoever else shares its NAT.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_CredentialedRequest_IsNeverRefusedHere()
    {
        var middleware = Create(out var store, out var reached);
        Drain(store, "anon:203.0.113.7");

        var context = CreateContext(
            remoteAddress: IPAddress.Parse("203.0.113.7"),
            credential: "sk-live-key");
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        reached.Value.Should().BeTrue();
    }

    /// <summary>Each address is bounded on its own; one flood cannot refuse another caller.</summary>
    [Fact]
    public async Task InvokeAsync_PartitionsByClientAddress()
    {
        var middleware = Create(out var store, out _);
        Drain(store, "anon:203.0.113.7");

        var innocent = CreateContext(remoteAddress: IPAddress.Parse("203.0.113.8"));
        await middleware.InvokeAsync(innocent);

        innocent.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// The admin API is reachable without a key too, and its 401s used to be free: the limiter
    /// proper sits behind security, so nothing counted them.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AdminApi_IsGuardedToo()
    {
        var middleware = Create(out var store, out _);
        Drain(store, "anon:203.0.113.7");

        var context = CreateContext(remoteAddress: IPAddress.Parse("203.0.113.7"), path: "/admin/api/keys");
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// A request the security layer refuses never reaches RateLimitMiddleware, so if this did not
    /// charge it nothing would, and an uncredentialed caller could take 401s as fast as it liked.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenSecurityRefusesTheRequest_ChargesTheAnonymousBucket()
    {
        var middleware = Create(
            out var store,
            out _,
            downstream: context =>
            {
                context.Items[GatewayAuthContextItems.CredentialRejected] = true;
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            });

        for (var i = 0; i < Anonymous.Capacity; i++)
        {
            var context = CreateContext(remoteAddress: IPAddress.Parse("203.0.113.7"));
            await middleware.InvokeAsync(context);
            context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        }

        var refused = CreateContext(remoteAddress: IPAddress.Parse("203.0.113.7"));
        await middleware.InvokeAsync(refused);
        refused.Response.StatusCode.Should()
            .Be(StatusCodes.Status429TooManyRequests, "the 401s spent the address's anonymous budget");
    }

    /// <summary>
    /// A request that is served is charged by RateLimitMiddleware against this very partition, so
    /// charging it here as well would bill one request twice.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenTheRequestIsServed_ChargesNothing()
    {
        var middleware = Create(out var store, out _);

        for (var i = 0; i < 200; i++)
        {
            await middleware.InvokeAsync(CreateContext(remoteAddress: IPAddress.Parse("203.0.113.7")));
        }

        store.PeekRequest(RateLimitKeys.Tenant("anon:203.0.113.7"), Anonymous, DateTimeOffset.UtcNow)
            .IsAcquired.Should().BeTrue("the limiter proper owns the accounting for a served request");
    }

    [Fact]
    public async Task InvokeAsync_NonInferencePath_IsNotGuarded()
    {
        var middleware = Create(out var store, out var reached);
        Drain(store, "anon:203.0.113.7");

        var context = CreateContext(remoteAddress: IPAddress.Parse("203.0.113.7"), path: "/v1/models");
        context.Request.Method = HttpMethods.Get;
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        reached.Value.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_WhenRateLimitingDisabled_PassesEverythingThrough()
    {
        var middleware = Create(out var store, out _, enabled: false);
        Drain(store, "anon:203.0.113.7");

        var context = CreateContext(remoteAddress: IPAddress.Parse("203.0.113.7"));
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// The refusal has to tell the client the same thing the limiter proper would: which budget
    /// refused it, how much is left, and when to come back.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Refusal_CarriesTheBudgetHeadersAndRetryAfter()
    {
        var middleware = Create(out var store, out _);
        Drain(store, "anon:203.0.113.7");

        var context = CreateContext(remoteAddress: IPAddress.Parse("203.0.113.7"));
        await middleware.InvokeAsync(context);

        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("rate_limit_exceeded");
        context.Response.Headers[GatewayHeaders.RateLimitLimit].ToString().Should().Be(Anonymous.Capacity.ToString());
        context.Response.Headers[GatewayHeaders.RateLimitRemaining].ToString().Should().Be("0");
        context.Response.Headers[GatewayHeaders.RetryAfter].ToString().Should().NotBeNullOrEmpty();
        context.Response.Headers[GatewayHeaders.RateLimitScope].ToString().Should()
            .Be("tenant", "the same scope the limiter proper would name for this partition");
    }

    private static void Drain(InMemoryDistributedRateLimitStore store, string partition)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < Anonymous.Capacity; i++)
        {
            store.DebitRequest(RateLimitKeys.Tenant(partition), Anonymous, now);
        }
    }

    private static DefaultHttpContext CreateContext(
        IPAddress? remoteAddress = null,
        string path = "/v1/chat/completions",
        string? credential = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remoteAddress;

        if (credential is not null)
        {
            context.Request.Headers[GatewayCredential.ApiKeyHeader] = credential;
        }

        return context;
    }

    private static AnonymousAdmissionGuardMiddleware Create(
        out InMemoryDistributedRateLimitStore store,
        out StrongBox reachedDownstream,
        bool enabled = true,
        Action<HttpContext>? downstream = null)
    {
        var resolver = new RateLimitPolicyResolver(
            new StubConfigProvider(new GatewayConfigSnapshot
            {
                RateLimits = new RateLimitsConfigSection
                {
                    Enabled = enabled,
                    Default = new RateLimitPolicy(3000, 0, 0),
                    Anonymous = Anonymous,
                },
            }),
            new StubAuthState { IsAuthenticationRequired = true });

        store = new InMemoryDistributedRateLimitStore();
        var reached = new StrongBox();
        reachedDownstream = reached;

        return new AnonymousAdmissionGuardMiddleware(
            context =>
            {
                reached.Value = true;
                if (downstream is null)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                }
                else
                {
                    downstream(context);
                }

                return Task.CompletedTask;
            },
            resolver,
            store,
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            governor: null,
            TimeProvider.System);
    }

    private sealed class StrongBox
    {
        public bool Value { get; set; }
    }

    private sealed class StubAuthState : IGatewayAuthenticationState
    {
        public bool IsAuthenticationRequired { get; init; }
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }
}
