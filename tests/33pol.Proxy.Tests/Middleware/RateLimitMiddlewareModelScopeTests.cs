using System.Text;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Parsing;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// The model-aware half of admission: when the body is read, which scopes are charged, and what is
/// given back when a narrow limit refuses a request the wide ones already admitted.
/// </summary>
public sealed class RateLimitMiddlewareModelScopeTests
{
    /// <summary>
    /// A per-model rule bites even though the tenant is nowhere near its own limit — the scopes
    /// compose, so the narrowest one wins.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenThePerModelRuleIsExhausted_Refuses()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = Create(
            store,
            new RateLimitsConfigSection
            {
                Default = new RateLimitPolicy(1000, 0, 0),
                Models = Map(("gpt-4", new RateLimitPolicy(1, 0, 0))),
            },
            out var nextCalls);

        (await Invoke(middleware, "gpt-4")).StatusCode.Should().Be(StatusCodes.Status200OK);

        var refused = await Invoke(middleware, "gpt-4");

        refused.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Headers[GatewayHeaders.RateLimitScope].ToString().Should().Be("model");
        nextCalls().Should().Be(1);
    }

    /// <summary>
    /// The scope header is what makes a multi-limit 429 actionable: a client that sees 0 remaining
    /// otherwise cannot tell whether it is its own key, its whole organisation, or the model it
    /// picked that ran out — and those call for three different responses.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ReportsTheTightestScope_OnASuccessfulRequest()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = Create(
            store,
            new RateLimitsConfigSection
            {
                Default = new RateLimitPolicy(1000, 0, 0),
                Models = Map(("gpt-4", new RateLimitPolicy(10, 0, 0))),
            },
            out _);

        var response = await Invoke(middleware, "gpt-4");

        response.Headers[GatewayHeaders.RateLimitScope].ToString().Should().Be("model");
        response.Headers[GatewayHeaders.RateLimitLimit].ToString().Should().Be("10");
        response.Headers[GatewayHeaders.RateLimitRemaining].ToString().Should().Be("9");
    }

    /// <summary>
    /// The refund. A caller blocked by a narrow per-model rule must not also be spending its
    /// tenant-wide budget on every attempt — otherwise one throttled model eventually rate-limits
    /// that tenant everywhere else too, purely because the client retried.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenTheModelScopeRefuses_TheTenantBudgetIsUntouched()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var tenantTier = new RateLimitPolicy(100, 0, 0);
        var middleware = Create(
            store,
            new RateLimitsConfigSection
            {
                Default = tenantTier,
                Models = Map(("gpt-4", new RateLimitPolicy(1, 0, 0))),
            },
            out _);

        await Invoke(middleware, "gpt-4");

        for (var i = 0; i < 50; i++)
        {
            (await Invoke(middleware, "gpt-4")).StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        }

        var tenantBudget = store.PeekRequest(
            RateLimitKeys.Tenant(RateLimitPartition.UnknownAnonymousKey),
            tenantTier,
            TimeProvider.System.GetUtcNow());

        tenantBudget.Remaining.Should().Be(99, "only the one admitted request cost a tenant token");
    }

    /// <summary>
    /// Per-model rules are keyed on canonical ids, so an alias must resolve before the rule is looked
    /// up. Matching on the raw name the client typed would let any alias walk straight past the limit
    /// set for the model behind it.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ARequestUsingAnAlias_IsChargedToTheCanonicalModel()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = Create(
            store,
            new RateLimitsConfigSection
            {
                Default = new RateLimitPolicy(1000, 0, 0),
                Models = Map(("gpt-4", new RateLimitPolicy(1, 0, 0))),
            },
            out _,
            aliases: ("gpt-4-latest", "gpt-4"));

        (await Invoke(middleware, "gpt-4")).StatusCode.Should().Be(StatusCodes.Status200OK);

        var viaAlias = await Invoke(middleware, "gpt-4-latest");

        viaAlias.StatusCode.Should().Be(
            StatusCodes.Status429TooManyRequests,
            "the alias draws on the same bucket as the model it names");
    }

    /// <summary>
    /// Learning the model means buffering and parsing the body. A gateway with no per-model rules
    /// must not pay for that: the parse is left to the router, exactly as before scoped rules
    /// existed.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WithNoModelScopedRules_DoesNotParseTheBody()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = Create(
            store,
            new RateLimitsConfigSection { Default = new RateLimitPolicy(1000, 0, 0) },
            out _);

        var context = CreateContext("gpt-4");
        await middleware.InvokeAsync(context);

        InferenceRequestParseCache.TryGet(context, out _).Should()
            .BeFalse("nothing needed the model, so nothing read the body");
    }

    /// <summary>
    /// A caller already over its tenant budget must be refused before the gateway pays to read what
    /// it sent — otherwise the cheapest thing a limiter does becomes the most expensive.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenTheTenantScopeRefuses_TheBodyIsNeverParsed()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = Create(
            store,
            new RateLimitsConfigSection
            {
                Default = new RateLimitPolicy(1, 0, 0),
                Models = Map(("gpt-4", new RateLimitPolicy(1000, 0, 0))),
            },
            out _);

        await Invoke(middleware, "gpt-4");

        var refused = CreateContext("gpt-4");
        await middleware.InvokeAsync(refused);

        refused.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Response.Headers[GatewayHeaders.RateLimitScope].ToString().Should().Be("tenant");
        InferenceRequestParseCache.TryGet(refused, out _).Should()
            .BeFalse("the first stage refused before anything read the body");
    }

    /// <summary>
    /// A body naming no model, or an unknown one, has no per-model rule by definition — but it must
    /// still be charged against the scopes that do apply, or a caller gets free requests by naming a
    /// model that does not exist.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AnUnknownModel_IsStillChargedToTheTenant()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = Create(
            store,
            new RateLimitsConfigSection
            {
                Default = new RateLimitPolicy(1, 0, 0),
                Models = Map(("gpt-4", new RateLimitPolicy(1000, 0, 0))),
            },
            out _);

        await Invoke(middleware, "no-such-model");

        var refused = await Invoke(middleware, "no-such-model");
        refused.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    private static RateLimitMiddleware Create(
        IDistributedRateLimitStore store,
        RateLimitsConfigSection rateLimits,
        out Func<int> nextCalls,
        (string Alias, string Canonical)? aliases = null)
    {
        var calls = 0;
        nextCalls = () => calls;

        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel(Arg.Any<string>(), out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                var requested = (string)call[0];
                var canonical = aliases is { } a && string.Equals(requested, a.Alias, StringComparison.OrdinalIgnoreCase)
                    ? a.Canonical
                    : requested;

                if (!string.Equals(canonical, "gpt-4", StringComparison.OrdinalIgnoreCase))
                {
                    call[1] = null;
                    return false;
                }

                call[1] = new ModelConfig { Id = canonical, Url = "http://backend:8000" };
                return true;
            });

        return new RateLimitMiddleware(
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            },
            new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits })),
            store,
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            registry,
            timeProvider: TimeProvider.System);
    }

    private static async Task<HttpResponse> Invoke(RateLimitMiddleware middleware, string model)
    {
        var context = CreateContext(model);
        await middleware.InvokeAsync(context);
        return context.Response;
    }

    private static DefaultHttpContext CreateContext(string model)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($$"""{"model":"{{model}}"}"""));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static IReadOnlyDictionary<string, RateLimitPolicy> Map(
        params (string Key, RateLimitPolicy Policy)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Policy, StringComparer.OrdinalIgnoreCase);

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }
}
