using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
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
            RejectCredential,
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
            Answer(StatusCodes.Status200OK),
            out _);

        for (var i = 0; i < 50; i++)
        {
            (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status200OK);
        }
    }

    /// <summary>
    /// A 403 is a recognised key without a grant or a role, never a guessed credential. Charging it
    /// let a valid key lock its own address out by asking for a model it was not granted.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Downstream403_IsNeverCharged()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            Answer(StatusCodes.Status403Forbidden),
            out _);

        for (var i = 0; i < 50; i++)
        {
            (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>
    /// The forwarder copies the upstream's status onto the response verbatim. An upstream whose own
    /// credential has expired answers 401 to every request; charging those spent every client
    /// address's budget on an outage none of them caused.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Downstream401WithoutTheSecurityMarker_IsNeverCharged()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            Answer(StatusCodes.Status401Unauthorized),
            out _);

        for (var i = 0; i < 50; i++)
        {
            (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status401Unauthorized);
        }
    }

    /// <summary>Each client address gets its own budget; one caller's guessing cannot lock out another.</summary>
    [Fact]
    public async Task InvokeAsync_PartitionsByClientAddress()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            RejectCredential,
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
        var middleware = CreateMiddleware(policy, RejectCredential, out var store);

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
            RejectCredential,
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
            RejectCredential,
            out _,
            enabled: false);

        for (var i = 0; i < 20; i++)
        {
            (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status401Unauthorized);
        }
    }

    /// <summary>
    /// A spent budget refuses only what cannot prove a credential. A key that validates still
    /// passes — refusing it too made a shared address a lockout for everyone behind it, admin
    /// access included — and passing it spends nothing, so the guesser stays refused.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ExhaustedBudget_ValidCredential_PassesThroughUncharged()
    {
        Action<HttpContext> downstream = RejectCredential;
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            context => downstream(context),
            out _);

        (await InvokeAsync(middleware, "/admin/api/keys")).Should().Be(StatusCodes.Status401Unauthorized);

        downstream = Answer(StatusCodes.Status200OK);
        var holder = CreateContext("/admin/api/keys", authentication: Authenticated());
        await middleware.InvokeAsync(holder);
        holder.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "a valid key is never locked out");

        downstream = RejectCredential;
        var guesser = CreateContext("/admin/api/keys", authentication: Unauthenticated());
        await middleware.InvokeAsync(guesser);
        guesser.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests, "the budget stays spent");
    }

    [Fact]
    public async Task InvokeAsync_ExhaustedBudget_NoCredential_Is429()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            RejectCredential,
            out _);

        await InvokeAsync(middleware, "/v1/chat/completions");

        var refused = CreateContext("/v1/chat/completions", authentication: Unauthenticated());
        await middleware.InvokeAsync(refused);

        refused.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Response.Headers[GatewayHeaders.RetryAfter].ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task InvokeAsync_ExhaustedBudget_FailedCredential_Is429()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            RejectCredential,
            out _);

        await InvokeAsync(middleware, "/v1/chat/completions");

        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), GatewayAuthSchemes.ApiKey)
            .Returns(AuthenticateResult.Fail("invalid_api_key"));

        (await InvokeAsync(middleware, "/v1/chat/completions", authentication: authentication))
            .Should().Be(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>A host with no authentication service can vouch for no one, so it refuses.</summary>
    [Fact]
    public async Task InvokeAsync_ExhaustedBudget_WithoutAnAuthenticationService_Is429()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            RejectCredential,
            out _);

        await InvokeAsync(middleware, "/v1/chat/completions");

        (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>A registered service without the gateway's scheme throws; that is "cannot prove" too.</summary>
    [Fact]
    public async Task InvokeAsync_ExhaustedBudget_WhenTheSchemeIsNotRegistered_Is429()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            RejectCredential,
            out _);

        await InvokeAsync(middleware, "/v1/chat/completions");

        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>())
            .ThrowsAsync(new InvalidOperationException("No authentication handler is registered for the scheme 'ApiKey'."));

        (await InvokeAsync(middleware, "/v1/chat/completions", authentication: authentication))
            .Should().Be(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// Authentication is only consulted on the refusal path. With budget left, the request goes
    /// straight through and the security middleware is the one that authenticates it.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WithBudgetLeft_DoesNotConsultAuthentication()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 5, Burst: 0, MaxConcurrentStreams: 0),
            Answer(StatusCodes.Status200OK),
            out _);

        var authentication = Unauthenticated();
        (await InvokeAsync(middleware, "/v1/chat/completions", authentication: authentication))
            .Should().Be(StatusCodes.Status200OK);

        await authentication.DidNotReceive().AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>());
    }

    /// <summary>What the security layer does when it refuses a credential: a 401 and the marker.</summary>
    private static void RejectCredential(HttpContext context)
    {
        context.Items[GatewayAuthContextItems.CredentialRejected] = true;
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    /// <summary>A downstream that answers a status without the security layer's marker.</summary>
    private static Action<HttpContext> Answer(int statusCode) =>
        context => context.Response.StatusCode = statusCode;

    private static IAuthenticationService Authenticated()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(GatewayAuthClaims.TenantId, Guid.NewGuid().ToString())],
            GatewayAuthSchemes.ApiKey));
        var ticket = new AuthenticationTicket(principal, GatewayAuthSchemes.ApiKey);

        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), GatewayAuthSchemes.ApiKey)
            .Returns(AuthenticateResult.Success(ticket));
        return authentication;
    }

    private static IAuthenticationService Unauthenticated()
    {
        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), GatewayAuthSchemes.ApiKey)
            .Returns(AuthenticateResult.NoResult());
        return authentication;
    }

    private static async Task<int> InvokeAsync(
        AuthFailureRateLimitMiddleware middleware,
        string path,
        IPAddress? remoteAddress = null,
        IAuthenticationService? authentication = null)
    {
        var context = CreateContext(path, remoteAddress, authentication);
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    private static DefaultHttpContext CreateContext(
        string path,
        IPAddress? remoteAddress = null,
        IAuthenticationService? authentication = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remoteAddress;

        if (authentication is not null)
        {
            context.RequestServices = new ServiceCollection()
                .AddSingleton(authentication)
                .BuildServiceProvider();
        }

        return context;
    }

    private static AuthFailureRateLimitMiddleware CreateMiddleware(
        RateLimitPolicy policy,
        Action<HttpContext> downstream,
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
                downstream(context);
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
