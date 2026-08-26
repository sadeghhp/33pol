using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Identity;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using Pol33.Proxy.Forwarding;
using Pol33.Proxy.Middleware;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// How the router draws on the rate-limit store: which tier it asks for, and the release of a
/// stream slot on every exit path.
/// </summary>
public sealed class ModelRouterRateLimitTests
{
    /// <summary>
    /// The tier is selected from the tenant slug, not from the partition key. They are the same
    /// string for authenticated traffic, but an anonymous partition is "anon:&lt;address&gt;", and
    /// passing that as a tenant slug looks for a per-tenant override under a key no tenant can hold
    /// — so the router would pick a different tier than RateLimitMiddleware did for the same
    /// request.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ResolvesTheTierFromTheTenantSlug_NotThePartitionKey()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var resolver = CreateResolver();
            var middleware = ModelRouterMiddlewareTests.CreateMiddlewareForRateLimitTests(
                registry: registry,
                forwarder: CreateForwarderReturning(ForwarderError.None),
                rateLimitPolicyResolver: resolver);

            var tenantId = Guid.NewGuid();

            // Streaming: the tier is resolved where it is needed, and the only control the router
            // enforces is the concurrency cap, which non-streaming requests never touch.
            var context = CreateContext("""{"model":"m1","stream":true}""");
            SetInferenceTenant(context, tenantId, planSlug: "enterprise");

            await middleware.InvokeAsync(context);

            resolver.Received().Resolve("enterprise", tenantId.ToString(), null);
        });
    }

    /// <summary>An anonymous caller has no tenant, so no per-tenant override can be looked up for it.</summary>
    [Fact]
    public async Task InvokeAsync_AnonymousRequest_ResolvesTheTierWithNoTenantSlug()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var resolver = CreateResolver();
            var middleware = ModelRouterMiddlewareTests.CreateMiddlewareForRateLimitTests(
                registry: registry,
                forwarder: CreateForwarderReturning(ForwarderError.None),
                rateLimitPolicyResolver: resolver);

            await middleware.InvokeAsync(CreateContext("""{"model":"m1","stream":true}"""));

            resolver.Received().Resolve(null, null, null);
        });
    }

    /// <summary>
    /// A stream slot is held for the length of the forward, so every way out of the forward has to
    /// give it back — an exception included. A leaked slot is permanent: the partition's stream cap
    /// shrinks by one for the lifetime of the process.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenTheForwardThrows_TheStreamSlotIsStillReleased()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var forwarder = Substitute.For<IInferenceHttpForwarder>();
            forwarder.SendAsync(
                    Arg.Any<HttpContext>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<StreamingHttpTransformer>(),
                    Arg.Any<bool>(),
                    Arg.Any<InferenceForwardTimeouts>(),
                    Arg.Any<CancellationToken>())
                .Returns<ForwarderError>(_ => throw new InvalidOperationException("boom"));

            // A real store rather than a substitute: what matters is that the slot is genuinely
            // free afterwards, not that some particular release method was called. Asserting the
            // call would have to be rewritten every time the release path changes shape, and would
            // still pass if the release named the wrong partition.
            var store = new Pol33.Policy.RateLimiting.InMemoryDistributedRateLimitStore();
            var oneStreamOnly = new RateLimitPolicy(10_000, 0, MaxConcurrentStreams: 1);
            var resolver = Substitute.For<IRateLimitPolicyResolver>();
            resolver.IsEnabled().Returns(true);
            resolver.Resolve(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(oneStreamOnly);

            var middleware = ModelRouterMiddlewareTests.CreateMiddlewareForRateLimitTests(
                registry: registry,
                forwarder: forwarder,
                rateLimitPolicyResolver: resolver,
                rateLimitStore: store);

            var tenantId = Guid.NewGuid();
            var context = CreateContext("""{"model":"m1","stream":true}""");
            SetInferenceTenant(context, tenantId, planSlug: null);

            var act = () => middleware.InvokeAsync(context);

            await act.Should().ThrowAsync<InvalidOperationException>();

            store.TryAcquireStreamSlot(tenantId.ToString(), oneStreamOnly).IsAcquired.Should()
                .BeTrue("the slot the failed forward held must be back");
        });
    }

    /// <summary>A non-streaming request never takes a slot, so it must never give one back either.</summary>
    [Fact]
    public async Task InvokeAsync_NonStreamingRequest_TakesNoStreamSlot()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var store = Substitute.For<IDistributedRateLimitStore>();
            store.TryAcquireStreamSlot(Arg.Any<string>(), Arg.Any<RateLimitPolicy>())
                .Returns(new RateLimitAcquireResult(true));

            var middleware = ModelRouterMiddlewareTests.CreateMiddlewareForRateLimitTests(
                registry: registry,
                forwarder: CreateForwarderReturning(ForwarderError.None),
                rateLimitStore: store);

            await middleware.InvokeAsync(CreateContext("""{"model":"m1"}"""));

            store.DidNotReceive().TryAcquireStreamSlot(Arg.Any<string>(), Arg.Any<RateLimitPolicy>());
            store.DidNotReceive().ReleaseStreamSlot(Arg.Any<string>());
        });
    }


    /// <summary>
    /// A streaming request refused by the concurrency cap is a refused request, and the governor has
    /// to hear about it. It never used to: the rate limiter reported an admission a few frames
    /// earlier, which clears the partition's backoff, and the router reported nothing at all — so a
    /// client pinned on a concurrency cap sat at zero consecutive rejections forever and its
    /// <c>Retry-After</c> never escalated, however hard it retried.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenTheConcurrencyCapRefuses_TheGovernorHearsAboutIt()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var governor = Substitute.For<IAdaptiveRateLimitGovernor>();
            var usage = new RecordingUsageTracker();

            var store = Substitute.For<IDistributedRateLimitStore>();
            store.TryAcquireStreamSlot(Arg.Any<string>(), Arg.Any<RateLimitPolicy>())
                .Returns(new RateLimitAcquireResult(
                    false,
                    GatewayRateLimitReason.ConcurrencyLimitExceeded,
                    RetryAfterSeconds: 1,
                    Limit: 4,
                    Remaining: 0,
                    Scope: RateLimitScope.Tenant));

            var middleware = ModelRouterMiddlewareTests.CreateMiddlewareForRateLimitTests(
                registry: registry,
                forwarder: CreateForwarderReturning(ForwarderError.None),
                rateLimitStore: store,
                rateLimitGovernor: governor,
                rateLimitUsage: usage);

            var context = CreateContext("""{"model":"m1","stream":true}""");
            await middleware.InvokeAsync(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            governor.Received(1).RecordOutcome(Arg.Any<string>(), false, Arg.Any<DateTimeOffset>());
            governor.DidNotReceive().RecordOutcome(Arg.Any<string>(), true, Arg.Any<DateTimeOffset>());

            // The slot count is not a per-minute rate. Passing it as one wrote MaxConcurrentStreams
            // into the usage report's rpm columns, and the tier a key is held to is last-writer-wins.
            var recorded = usage.Events.Should().ContainSingle().Subject;
            recorded.Control.Should().Be(RateLimitControl.Concurrency);
            recorded.ConfiguredRpm.Should().Be(0);
            recorded.EffectiveRpm.Should().Be(0);
        });
    }

    /// <summary>
    /// The mirror image: a request that clears every gate the router applies is the one that clears
    /// the backoff, because this is the last place a request can still be refused.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenTheRequestPassesEveryGate_TheGovernorSeesTheAdmission()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var governor = Substitute.For<IAdaptiveRateLimitGovernor>();

            var middleware = ModelRouterMiddlewareTests.CreateMiddlewareForRateLimitTests(
                registry: registry,
                forwarder: CreateForwarderReturning(ForwarderError.None),
                rateLimitGovernor: governor);

            await middleware.InvokeAsync(CreateContext("""{"model":"m1"}"""));

            governor.Received(1).RecordOutcome(Arg.Any<string>(), true, Arg.Any<DateTimeOffset>());
        });
    }

    private sealed class RecordingUsageTracker : IRateLimitUsageTracker
    {
        public List<RateLimitUsageEvent> Events { get; } = [];

        public void Record(in RateLimitUsageEvent usageEvent) => Events.Add(usageEvent);

        public RateLimitUsageReport BuildReport(int minutes, int take, DateTimeOffset now) =>
            throw new NotSupportedException();

        public void Reset() => Events.Clear();
    }

    private static IRateLimitPolicyResolver CreateResolver()
    {
        var resolver = Substitute.For<IRateLimitPolicyResolver>();
        resolver.Resolve(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new RateLimitPolicy(10_000, 1_000, 1_000));
        resolver.IsEnabled().Returns(true);
        return resolver;
    }

    private static IInferenceHttpForwarder CreateForwarderReturning(ForwarderError error)
    {
        var forwarder = Substitute.For<IInferenceHttpForwarder>();
        forwarder.SendAsync(
                Arg.Any<HttpContext>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<StreamingHttpTransformer>(),
                Arg.Any<bool>(),
                Arg.Any<InferenceForwardTimeouts>(),
                Arg.Any<CancellationToken>())
            .Returns(error);
        return forwarder;
    }

    private static DefaultHttpContext CreateContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static void SetInferenceTenant(DefaultHttpContext context, Guid tenantId, string? planSlug)
    {
        var apiKeyId = Guid.NewGuid();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(GatewayAuthClaims.TenantId, tenantId.ToString()),
                new Claim(GatewayAuthClaims.ApiKeyId, apiKeyId.ToString()),
                new Claim(GatewayAuthClaims.Role, ApiKeyRole.Inference.ToString()),
            ],
            GatewayAuthSchemes.ApiKey));

        context.Items[TenantContextKeys.HttpContextItemKey] = new TenantContext
        {
            TenantId = tenantId.ToString(),
            ApiKeyId = apiKeyId.ToString(),
            Role = ApiKeyRole.Inference,
            PlanSlug = planSlug,
        };
    }

    private static async Task WithSingleModelRegistryAsync(
        Func<Pol33.Registry.Services.ModelRegistryService, Task> body)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-rl-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, """
            { "models": [ { "id": "m1", "url": "http://backend:8000", "aliases": [] } ] }
            """);

        try
        {
            var registry = new Pol33.Registry.Services.ModelRegistryService(
                NullLogger<Pol33.Registry.Services.ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(configPath);
            await body(registry);
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
