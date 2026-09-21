using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Routing;

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

    /// <summary>
    /// The master switch is how an operator stops shaping client traffic during an incident. It used
    /// to switch this off as well, so the one action taken under pressure also removed the only
    /// ceiling on credential guessing.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenRateLimitingDisabled_StillBoundsGuessing()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            RejectCredential,
            out _,
            enabled: false);

        (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status401Unauthorized);
        (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>Its own switch, for a deployment that deliberately sends bad keys (a load test).</summary>
    [Fact]
    public async Task InvokeAsync_WhenProtectionDisabled_NeverRejects()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            RejectCredential,
            out _,
            protectionEnabled: false);

        for (var i = 0; i < 20; i++)
        {
            (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status401Unauthorized);
        }
    }

    /// <summary>
    /// A caller offering no credential is not guessing at one. Charging it made anonymous traffic to
    /// a public model spend a budget it could never be admitted by, and refusing it made one stale
    /// key behind a NAT a lockout for every anonymous caller sharing that address.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WithoutACredential_IsNeitherChargedNorRefused()
    {
        var policy = new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0);
        var middleware = CreateMiddleware(policy, RejectCredential, out var store);

        for (var i = 0; i < 20; i++)
        {
            (await InvokeAsync(middleware, "/v1/chat/completions", credential: null))
                .Should().Be(StatusCodes.Status401Unauthorized);
        }

        store.PeekRequest("authfail:unknown", policy, DateTimeOffset.UtcNow).IsAcquired.Should()
            .BeTrue("an uncredentialed request spends nothing, so a credentialed one still has its budget");
    }

    /// <summary>
    /// Proving a credential is not free: an unknown key misses the validator's caches and costs a
    /// database read. Without a bound on it, an attacker rotating keys past the budget kept forcing
    /// that read on every guess and the limiter only shortened the reply.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_PastTheProbeAllowance_StopsValidatingAltogether()
    {
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            RejectCredential,
            out _,
            probeMultiplier: 2);

        // Spends the auth-failure budget; every request after this one has to prove a credential.
        (await InvokeAsync(middleware, "/v1/chat/completions")).Should().Be(StatusCodes.Status401Unauthorized);

        var authentication = Unauthenticated();
        for (var i = 0; i < 10; i++)
        {
            (await InvokeAsync(middleware, "/v1/chat/completions", authentication: authentication))
                .Should().Be(StatusCodes.Status429TooManyRequests);
        }

        await authentication.Received(2).AuthenticateAsync(Arg.Any<HttpContext>(), GatewayAuthSchemes.ApiKey);
    }

    /// <summary>
    /// A key that authenticates is answered from the validator's positive cache, so proving it costs
    /// nothing and must not count against the allowance — otherwise the bound on wasted work would
    /// become a second, much lower rate limit on the legitimate clients behind a shared address.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_PastTheBudget_AValidCredential_NeverSpendsTheProbeAllowance()
    {
        Action<HttpContext> downstream = RejectCredential;
        var middleware = CreateMiddleware(
            new RateLimitPolicy(Rpm: 1, Burst: 0, MaxConcurrentStreams: 0),
            context => downstream(context),
            out _,
            probeMultiplier: 2);

        (await InvokeAsync(middleware, "/admin/api/keys")).Should().Be(StatusCodes.Status401Unauthorized);

        downstream = Answer(StatusCodes.Status200OK);
        var holder = Authenticated();
        for (var i = 0; i < 25; i++)
        {
            (await InvokeAsync(middleware, "/admin/api/keys", authentication: holder))
                .Should().Be(StatusCodes.Status200OK, "a valid key is never locked out, however long the flood lasts");
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

    /// <summary>A credential that does not authenticate is exactly what the budget is for.</summary>
    [Fact]
    public async Task InvokeAsync_ExhaustedBudget_UnprovableCredential_Is429()
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
        IAuthenticationService? authentication = null,
        string? credential = DefaultCredential)
    {
        var context = CreateContext(path, remoteAddress, authentication, credential);
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    /// <summary>
    /// The credential every case carries unless it is testing what happens without one. The limiter
    /// only looks at requests that present one, so a context without it exercises nothing.
    /// </summary>
    private const string DefaultCredential = "sk-guess";

    private static DefaultHttpContext CreateContext(
        string path,
        IPAddress? remoteAddress = null,
        IAuthenticationService? authentication = null,
        string? credential = DefaultCredential)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remoteAddress;

        if (credential is not null)
        {
            context.Request.Headers[GatewayCredential.ApiKeyHeader] = credential;
        }

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
        bool enabled = true,
        bool protectionEnabled = true,
        int probeMultiplier = 10,
        IRateLimitUsageTracker? usage = null)
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
            TimeProvider.System,
            Options.Create(new RateLimitingOptions
            {
                AuthFailureProtectionEnabled = protectionEnabled,
                AuthFailureProbeMultiplier = probeMultiplier,
            }),
            usage);
    }

    /// <summary>
    /// The protective row on the console is built from these three steps. A valid credential is
    /// checked and never charged; a rejected one is checked then charged; once the budget is spent a
    /// request that cannot prove itself is refused — and a refusal is not also a charge.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ReportsEachStepToTheUsageTracker()
    {
        var usage = new StepRecorder();
        var valid = CreateMiddleware(new RateLimitPolicy(1, 0, 0), Answer(StatusCodes.Status200OK), out _, usage: usage);
        await InvokeAsync(valid, "/v1/chat/completions");
        usage.Steps.Should().Equal((RateLimitAuthFailureStep.Checked, 1));

        usage.Steps.Clear();
        var guessing = CreateMiddleware(new RateLimitPolicy(1, 0, 0), RejectCredential, out _, usage: usage);
        await InvokeAsync(guessing, "/v1/chat/completions", authentication: Unauthenticated());
        var refused = await InvokeAsync(guessing, "/v1/chat/completions", authentication: Unauthenticated());

        refused.Should().Be(StatusCodes.Status429TooManyRequests);
        usage.Steps.Should().Equal(
            (RateLimitAuthFailureStep.Checked, 1),
            (RateLimitAuthFailureStep.Charged, 1),
            (RateLimitAuthFailureStep.Refused, 1));
    }

    private sealed class StepRecorder : IRateLimitUsageTracker
    {
        public List<(RateLimitAuthFailureStep Step, int Rpm)> Steps { get; } = [];

        public void RecordAuthFailure(RateLimitAuthFailureStep step, int enforcedRpm) => Steps.Add((step, enforcedRpm));

        public void Record(in RateLimitUsageEvent usageEvent)
        {
        }

        public RateLimitUsageReport BuildReport(int minutes, int take, DateTimeOffset now) => throw new NotSupportedException();

        public void Reset()
        {
        }
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// Credential guessing is bounded by the configured rate, not by the configured rate times
    /// however many requests the attacker keeps in flight.
    /// </summary>
    /// <remarks>
    /// <para>Admission here is decided from a <em>peek</em>, which does not consume a token, and the
    /// charge lands only once the security layer has said whether the credential was rejected. So a
    /// caller that opens many requests at once has all of them peek before any of them charges, and
    /// every one of them is admitted on the same single token.</para>
    ///
    /// <para>That part is inherent to charging for the outcome rather than the attempt, and it is
    /// bounded: it costs one round. What made it unbounded was the bucket forgiving the surplus —
    /// flooring at zero meant each refilled token bought another full round, so the sustained
    /// guessing rate was the configured rate multiplied by the concurrency. The bucket now carries
    /// the debt, so the round is paid for before another token is available.</para>
    /// </remarks>
    [Fact]
    public async Task InvokeAsync_ManyConcurrentRejections_AreChargedForRatherThanForgiven()
    {
        const int Concurrency = 40;
        var start = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var store = new InMemoryDistributedRateLimitStore(timeProvider: clock);

        // One token a second, and a bucket that starts with exactly one.
        var policy = new RateLimitPolicy(Rpm: 60, Burst: 0, MaxConcurrentStreams: 0);
        var resolver = new RateLimitPolicyResolver(new StubConfigProvider(new GatewayConfigSnapshot
        {
            RateLimits = new RateLimitsConfigSection { Enabled = true, Default = policy },
        }));

        // Every request is held inside the pipeline until all of them have been admitted, which is
        // what makes them concurrent from the limiter's point of view.
        var allInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;

        var middleware = new AuthFailureRateLimitMiddleware(
            async context =>
            {
                RejectCredential(context);
                if (Interlocked.Increment(ref arrived) == Concurrency)
                {
                    allInside.SetResult();
                }

                await allInside.Task;
            },
            resolver,
            store,
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            clock,
            Options.Create(new RateLimitingOptions()));

        // Spend the bucket down to its last token, one request at a time.
        for (var i = 0; i < 59; i++)
        {
            var warmup = CreateContext("/v1/chat/completions");
            RejectCredential(warmup);
            store.DebitRequest(RateLimitPartition.ResolveAuthFailure(warmup), policy, clock.GetUtcNow());
        }

        var inFlight = Enumerable
            .Range(0, Concurrency)
            .Select(_ => middleware.InvokeAsync(CreateContext("/v1/chat/completions")))
            .ToArray();

        await Task.WhenAll(inFlight);

        // The round got through — that much is inherent — but it was charged for. A second later
        // there is still no token, because the debt is being refilled away first.
        clock.Advance(TimeSpan.FromSeconds(1));
        (await InvokeAsync(middleware, "/v1/chat/completions", authentication: Unauthenticated()))
            .Should().Be(StatusCodes.Status429TooManyRequests);

        // Forgiven, the very next token would have bought another full round.
        clock.Advance(TimeSpan.FromSeconds(Concurrency - 2));
        (await InvokeAsync(middleware, "/v1/chat/completions", authentication: Unauthenticated()))
            .Should().Be(StatusCodes.Status429TooManyRequests);

        // Once the debt is paid the address is usable again, and no later than that.
        clock.Advance(TimeSpan.FromSeconds(3));
        (await InvokeAsync(middleware, "/v1/chat/completions", authentication: Unauthenticated()))
            .Should().Be(StatusCodes.Status401Unauthorized);
    }
}
