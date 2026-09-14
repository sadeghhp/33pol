using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
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

    /// <summary>
    /// The admin API and the model listing are work the gateway does itself, and nothing bounded
    /// them: a key could poll an endpoint that reads the database as fast as it liked.
    /// </summary>
    [Theory]
    [InlineData("/admin/api/keys", "POST")]
    [InlineData("/admin/api/overview", "GET")]
    [InlineData("/v1/models", "GET")]
    public async Task InvokeAsync_ControlPlanePath_IsMeteredAgainstTheCaller(string path, string method)
    {
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(10_000, 0, 0) },
            out var nextCalls,
            controlPlaneRpm: 1);

        (await InvokeAsync(middleware, path, method)).Should().Be(StatusCodes.Status200OK);
        (await InvokeAsync(middleware, path, method)).Should().Be(StatusCodes.Status429TooManyRequests);
        nextCalls().Should().Be(1);
    }

    /// <summary>
    /// A refusal carries the standard <c>RateLimit-*</c> names as well as the vendor-prefixed ones.
    /// </summary>
    /// <remarks>
    /// The prefix exists because an upstream provider\'s own budget headers are copied onto the
    /// response after the limiter has run, so an unprefixed name would be silently overwritten by a
    /// number about a different limit. That cannot happen on a refusal — a 429 the gateway writes
    /// never reaches an upstream — so on exactly the response a client most needs to read, the
    /// standard names were carrying nothing.
    /// </remarks>
    [Fact]
    public async Task InvokeAsync_Refusal_CarriesTheStandardRateLimitHeadersToo()
    {
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(1, 0, 0) },
            out _);

        await InvokeAsync(middleware, "/v1/chat/completions", "POST");

        var refused = CreateContext("/v1/chat/completions", "POST");
        await middleware.InvokeAsync(refused);

        refused.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Response.Headers[GatewayHeaders.StandardRateLimitLimit].ToString().Should().Be("1");
        refused.Response.Headers[GatewayHeaders.StandardRateLimitRemaining].ToString().Should().Be("0");
        refused.Response.Headers[GatewayHeaders.StandardRateLimitReset].ToString().Should().NotBeNullOrEmpty();

        // The prefixed ones are unchanged, so anything written against the gateway keeps working.
        refused.Response.Headers[GatewayHeaders.RateLimitLimit].ToString().Should().Be("1");
    }

    /// <summary>
    /// An admitted response keeps the prefixed names only: there an upstream\'s own budget headers
    /// land on the same response, and the unprefixed ones are theirs to set.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Admitted_CarriesOnlyThePrefixedHeaders()
    {
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(10, 0, 0) },
            out _);

        var admitted = CreateContext("/v1/chat/completions", "POST");
        await middleware.InvokeAsync(admitted);

        admitted.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        admitted.Response.Headers[GatewayHeaders.RateLimitLimit].ToString().Should().Be("10");
        admitted.Response.Headers.ContainsKey(GatewayHeaders.StandardRateLimitLimit).Should().BeFalse();
    }

    /// <summary>
    /// The control-plane budget is per credential, not per tenant.
    /// </summary>
    /// <remarks>
    /// Every operator key belongs to the one operator tenant, so a tenant-wide bucket was shared by
    /// every console session, wallboard and scripted admin client at once — a handful of open tabs
    /// polling twice a second reach it together, and the answer is a 429 on every admin call, which
    /// locks out the console that is the only place to see what is happening. The tier is an
    /// appsettings guard rail read once at startup, so there is no way to widen it from inside a
    /// running process either.
    /// </remarks>
    [Fact]
    public async Task InvokeAsync_OneConsoleSessionOverItsBudget_DoesNotRefuseTheOthers()
    {
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(10_000, 0, 0) },
            out _,
            controlPlaneRpm: 1);

        var noisy = CreateContext("/admin/api/overview", "GET", apiKeyId: "session-a");
        await middleware.InvokeAsync(noisy);
        noisy.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        var noisyAgain = CreateContext("/admin/api/overview", "GET", apiKeyId: "session-a");
        await middleware.InvokeAsync(noisyAgain);
        noisyAgain.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);

        // Same tenant, different credential: unaffected.
        var other = CreateContext("/admin/api/overview", "GET", apiKeyId: "session-b");
        await middleware.InvokeAsync(other);
        other.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    /// <summary>
    /// The static console is files, not API calls. Rate-limiting a page load would only break the
    /// console it is meant to protect.
    /// </summary>
    [Theory]
    [InlineData("/admin/index.html")]
    [InlineData("/health/live")]
    [InlineData("/metrics")]
    public async Task InvokeAsync_UnmeteredPath_IsNeverRefused(string path)
    {
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(1, 0, 0) },
            out var nextCalls);

        for (var i = 0; i < 10; i++)
        {
            (await InvokeAsync(middleware, path, "GET")).Should().Be(StatusCodes.Status200OK);
        }

        nextCalls().Should().Be(10);
    }

    /// <summary>
    /// Console polling and inference must not spend each other's budget. The global rule in
    /// particular is documented as a ceiling on inference traffic, and quietly spending it on an
    /// operator's console would make it mean something other than what it says.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ControlPlanePath_SpendsNeitherTheGlobalRuleNorTheInferenceBucket()
    {
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection
            {
                Global = new RateLimitPolicy(1, 0, 0),
                Default = new RateLimitPolicy(1, 0, 0),
            },
            out _);

        (await InvokeAsync(middleware, "/admin/api/overview", "GET")).Should().Be(StatusCodes.Status200OK);

        (await InvokeAsync(middleware, "/v1/chat/completions", "POST")).Should()
            .Be(StatusCodes.Status200OK, "the inference budget and the global rule were both untouched");
    }

    /// <summary>
    /// The reverse, and the reason the control plane does not use the caller's tier: an operator who
    /// sets a tight default tier must still be able to reach the console that would loosen it.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_InferenceAtItsLimit_DoesNotRefuseTheControlPlane()
    {
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(1, 0, 0) },
            out _);

        (await InvokeAsync(middleware, "/v1/chat/completions", "POST")).Should().Be(StatusCodes.Status200OK);
        (await InvokeAsync(middleware, "/v1/chat/completions", "POST")).Should()
            .Be(StatusCodes.Status429TooManyRequests);

        for (var i = 0; i < 20; i++)
        {
            (await InvokeAsync(middleware, "/admin/api/overview", "GET")).Should()
                .Be(StatusCodes.Status200OK, "the console has a budget of its own, and a roomy one");
        }
    }

    /// <summary>
    /// The usage report's rate columns are last-writer-wins, so a console poll passing through them
    /// would leave the tenant's "usage against limit" describing the 600 rpm control-plane budget
    /// rather than the tier its inference is held to. The report is about inference; the control
    /// plane stays out of it entirely, admitted or refused.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ControlPlanePath_NeverReachesTheUsageReport()
    {
        var usage = Substitute.For<IRateLimitUsageTracker>();
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(10_000, 0, 0) },
            out _,
            controlPlaneRpm: 1,
            usage: usage);

        (await InvokeAsync(middleware, "/admin/api/overview", "GET")).Should().Be(StatusCodes.Status200OK);
        (await InvokeAsync(middleware, "/admin/api/overview", "GET")).Should()
            .Be(StatusCodes.Status429TooManyRequests);

        usage.DidNotReceive().Record(Arg.Any<RateLimitUsageEvent>());
    }

    /// <summary>
    /// And the governor stays out of it too: it lengthens a partition's Retry-After the longer it
    /// keeps being refused, so routing console refusals through it would have a tenant's inference
    /// calls told to wait because its console was polling too fast.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ControlPlaneRefusal_DoesNotTouchTheCallersInferenceBackoff()
    {
        var governor = Substitute.For<IAdaptiveRateLimitGovernor>();
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(10_000, 0, 0) },
            out _,
            governor,
            controlPlaneRpm: 1);

        await InvokeAsync(middleware, "/admin/api/overview", "GET");
        (await InvokeAsync(middleware, "/admin/api/overview", "GET")).Should()
            .Be(StatusCodes.Status429TooManyRequests);

        governor.DidNotReceive().RecordOutcome(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>());
        governor.DidNotReceive().GetRetryAfterSeconds(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<DateTimeOffset>());
    }

    /// <summary>
    /// Every RateLimitScope names a dimension a rule can target, and this budget is not one of them.
    /// Labelling the answer "tenant" would point the client at the tenant tier, which is not the
    /// number it was refused by.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ControlPlaneRefusal_ReportsTheBudgetWithoutClaimingAScope()
    {
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(10_000, 0, 0) },
            out _,
            controlPlaneRpm: 1);

        await InvokeAsync(middleware, "/admin/api/overview", "GET");
        var refused = CreateContext("/admin/api/overview", "GET");
        await middleware.InvokeAsync(refused);

        refused.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Response.Headers[GatewayHeaders.RateLimitLimit].ToString().Should().Be("1");
        refused.Response.Headers[GatewayHeaders.RateLimitRemaining].ToString().Should().Be("0");
        refused.Response.Headers[GatewayHeaders.RetryAfter].ToString().Should().NotBeNullOrEmpty();
        refused.Response.Headers.ContainsKey(GatewayHeaders.RateLimitScope).Should().BeFalse();
    }

    /// <summary>Zero rpm and zero burst is how a deployment opts out of the control-plane ceiling.</summary>
    [Fact]
    public async Task InvokeAsync_ControlPlaneTierUnset_MetersNothing()
    {
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(1, 0, 0) },
            out _,
            controlPlaneRpm: 0);

        for (var i = 0; i < 20; i++)
        {
            (await InvokeAsync(middleware, "/admin/api/overview", "GET")).Should().Be(StatusCodes.Status200OK);
        }
    }

    /// <summary>
    /// The governor lengthens a partition's Retry-After the longer it keeps being refused, on the
    /// premise that it is retrying too fast. A global or model refusal is the gateway being busy, so
    /// escalating for it told a tenant well inside its own tier to wait a minute.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_RefusedByASharedScope_DoesNotEscalateTheCallersBackoff()
    {
        var governor = Substitute.For<IAdaptiveRateLimitGovernor>();
        var middleware = CreateMiddleware(
            new RateLimitsConfigSection
            {
                Global = new RateLimitPolicy(1, 0, 0),
                Default = new RateLimitPolicy(1000, 0, 0),
            },
            out _,
            governor);

        await InvokeAsync(middleware, "/v1/chat/completions", "POST");
        (await InvokeAsync(middleware, "/v1/chat/completions", "POST")).Should()
            .Be(StatusCodes.Status429TooManyRequests);

        governor.DidNotReceive().RecordOutcome(
            Arg.Any<string>(),
            admitted: false,
            Arg.Any<DateTimeOffset>());
        governor.DidNotReceive().GetRetryAfterSeconds(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<DateTimeOffset>());
    }

    /// <summary>A tenant over its own tier is exactly the case escalation exists for.</summary>
    [Fact]
    public async Task InvokeAsync_RefusedByTheCallersOwnScope_StillEscalates()
    {
        var governor = Substitute.For<IAdaptiveRateLimitGovernor>();
        governor.GetRetryAfterSeconds(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DateTimeOffset>()).Returns(7);

        var middleware = CreateMiddleware(
            new RateLimitsConfigSection { Default = new RateLimitPolicy(1, 0, 0) },
            out _,
            governor);

        await InvokeAsync(middleware, "/v1/chat/completions", "POST");
        var refused = CreateContext("/v1/chat/completions", "POST");
        await middleware.InvokeAsync(refused);

        refused.Response.Headers[GatewayHeaders.RetryAfter].ToString().Should().Be("7");
        governor.Received().RecordOutcome(Arg.Any<string>(), admitted: false, Arg.Any<DateTimeOffset>());
    }

    private static async Task<int> InvokeAsync(RateLimitMiddleware middleware, string path, string method)
    {
        var context = CreateContext(path, method);
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    private static DefaultHttpContext CreateContext(string path, string method, string? apiKeyId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        if (apiKeyId is not null)
        {
            context.Items[TenantContextKeys.HttpContextItemKey] = new TenantContext
            {
                TenantId = OperatorTenantId,
                ApiKeyId = apiKeyId,
                Role = ApiKeyRole.Admin,
            };
        }

        return context;
    }

    /// <summary>Every operator key the console issues belongs to this one tenant.</summary>
    private const string OperatorTenantId = "operator";

    private static RateLimitMiddleware CreateMiddleware(
        RateLimitsConfigSection rateLimits,
        out Func<int> nextCalls,
        IAdaptiveRateLimitGovernor? governor = null,
        int controlPlaneRpm = 600,
        IRateLimitUsageTracker? usage = null)
    {
        var calls = 0;
        nextCalls = () => calls;

        return new RateLimitMiddleware(
            context =>
            {
                calls++;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits })),
            new InMemoryDistributedRateLimitStore(),
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            Substitute.For<IModelRegistry>(),
            governor,
            usage,
            timeProvider: TimeProvider.System,
            options: Options.Create(new RateLimitingOptions
            {
                ControlPlane = new RateLimitTierOptions
                {
                    Rpm = controlPlaneRpm,
                    Burst = 0,
                    MaxConcurrentStreams = 0,
                },
            }));
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
