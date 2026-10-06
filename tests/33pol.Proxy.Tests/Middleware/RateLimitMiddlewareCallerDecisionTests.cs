using System.Text;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Observability;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// Every decision of the inference limiter is counted against the caller it was made for, so an
/// alert can say which team is being refused instead of only that somebody is.
/// </summary>
public sealed class RateLimitMiddlewareCallerDecisionTests
{
    private static readonly MetricCaller Fanus = new("fanus", "Fanus-MMT-Campaign");

    [Fact]
    public async Task InvokeAsync_AnAdmittedRequest_IsOneAdmittedDecisionForItsCaller()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var middleware = Create(new RateLimitPolicy(60, 0, 0), metrics);

        await Invoke(middleware, Tenant("fanus", "Fanus-MMT-Campaign"));

        metrics.Received(1).RecordRateLimitDecision(
            Arg.Is(Fanus), Arg.Any<string?>(), Arg.Any<RateLimitScope?>(), Arg.Is(RateLimitControl.Rate), Arg.Is(true));
        metrics.DidNotReceive().RecordRateLimitDecision(
            Arg.Any<MetricCaller>(), Arg.Any<string?>(), Arg.Any<RateLimitScope?>(), Arg.Any<RateLimitControl>(), Arg.Is(false));
    }

    [Fact]
    public async Task InvokeAsync_ARefusedRequest_IsARefusedDecisionNamingTheScope()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var middleware = Create(new RateLimitPolicy(1, 0, 0), metrics);
        var tenant = Tenant("fanus", "Fanus-MMT-Campaign");

        await Invoke(middleware, tenant);
        var refused = await Invoke(middleware, tenant);

        refused.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        metrics.Received(1).RecordRateLimitDecision(
            Fanus, null, RateLimitScope.Tenant, RateLimitControl.Rate, admitted: false);
    }

    /// <summary>
    /// The refusal series that existed before is kept as it was: the shipped refusal alert reads it,
    /// and it is the only one that counts the limiters with no caller to name.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ARefusedRequest_StillCountsOnTheReasonSeries()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var middleware = Create(new RateLimitPolicy(1, 0, 0), metrics);
        var tenant = Tenant("fanus", "Fanus-MMT-Campaign");

        await Invoke(middleware, tenant);
        await Invoke(middleware, tenant);

        metrics.Received(1).RecordRateLimitRejection("rate_limit:tenant", tenant.TenantId, null);
    }

    [Fact]
    public async Task InvokeAsync_AKeylessRequest_IsAnAnonymousDecision()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var middleware = Create(new RateLimitPolicy(60, 0, 0), metrics);

        await Invoke(middleware, tenant: null);

        metrics.Received(1).RecordRateLimitDecision(
            Arg.Is(MetricCaller.Anonymous), Arg.Any<string?>(), Arg.Any<RateLimitScope?>(), Arg.Is(RateLimitControl.Rate), Arg.Is(true));
    }

    /// <summary>
    /// With a model-scoped rule the body is parsed, so the decision can name the model — which is
    /// what lets a refusal on one model be told apart from the caller's tier being spent.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WithAModelRule_NamesTheModelOnTheDecision()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var middleware = Create(
            new RateLimitPolicy(1000, 0, 0), metrics, modelTier: new RateLimitPolicy(1, 0, 0));
        var tenant = Tenant("fanus", "Fanus-MMT-Campaign");

        await Invoke(middleware, tenant);
        await Invoke(middleware, tenant);

        metrics.Received(1).RecordRateLimitDecision(
            Arg.Is(Fanus), Arg.Is("gpt-4"), Arg.Any<RateLimitScope?>(), Arg.Is(RateLimitControl.Rate), Arg.Is(true));
        metrics.Received(1).RecordRateLimitDecision(
            Fanus, "gpt-4", RateLimitScope.Model, RateLimitControl.Rate, admitted: false);
    }

    private static TenantContext Tenant(string slug, string keyLabel) =>
        new()
        {
            TenantId = Guid.NewGuid().ToString(),
            ApiKeyId = Guid.NewGuid().ToString(),
            TenantSlug = slug,
            ApiKeyLabel = keyLabel,
        };

    private static RateLimitMiddleware Create(
        RateLimitPolicy tenantTier,
        IGatewayMetricsCollector metrics,
        RateLimitPolicy? modelTier = null)
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel(Arg.Any<string>(), out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "gpt-4", Url = "http://backend:8000" };
                return true;
            });

        var rateLimits = new RateLimitsConfigSection
        {
            Default = tenantTier,
            Models = modelTier is null
                ? new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase) { ["gpt-4"] = modelTier },
        };

        return new RateLimitMiddleware(
            _ => Task.CompletedTask,
            new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits })),
            new InMemoryDistributedRateLimitStore(),
            new OpenAiErrorResponseWriter(),
            metrics,
            registry);
    }

    private static async Task<HttpResponse> Invoke(RateLimitMiddleware middleware, TenantContext? tenant)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"model":"gpt-4"}"""));
        context.Response.Body = new MemoryStream();
        if (tenant is not null)
        {
            context.Items[TenantContextKeys.HttpContextItemKey] = tenant;
        }

        await middleware.InvokeAsync(context);
        return context.Response;
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }
}
