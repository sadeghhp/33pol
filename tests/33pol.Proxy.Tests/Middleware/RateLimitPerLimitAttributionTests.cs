using System.Text;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// What the middleware tells the per-limit report, through the real resolver and the real store:
/// which configured control was evaluated, which kept its token, and which one refused.
/// </summary>
public sealed class RateLimitPerLimitAttributionTests
{
    [Fact]
    public async Task ASingleLimit_IsChargedOncePerAdmittedRequest()
    {
        var usage = new StageRecorder();
        var middleware = Create(new RateLimitsConfigSection { Default = new(60, 0, 0) }, usage);

        (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status200OK);

        usage.Stages.Should().ContainSingle().Which.Should().Be((RateLimitStageOutcome.Charged, "default", null));
    }

    [Fact]
    public async Task WhenTheFirstStageRefuses_TheRefusingBucketIsNamed_AndTheModelLimitsAreNeverAsked()
    {
        var usage = new StageRecorder();
        var middleware = Create(
            new RateLimitsConfigSection
            {
                Default = new(60, 0, 0),
                TenantOverrides = Map(("acme", new(1, 0, 0))),
                Models = Map(("gpt-4", new(100, 0, 0))),
            },
            usage);

        await Invoke(middleware);
        usage.Stages.Clear();
        (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);

        usage.Stages.Should().ContainSingle().Which.Should().Be(
            (RateLimitStageOutcome.Refused, "tenant:acme", RateLimitKeys.Tenant(TenantId)));
    }

    /// <summary>
    /// Tenant and model overlap. When the model limit refuses, the tenant's token goes back, and the
    /// report has to say so: a tenant row that counted this as charged would show load the tenant's
    /// bucket never carried.
    /// </summary>
    [Fact]
    public async Task WhenTheModelLimitRefuses_TheTenantLimitIsReportedAsRefunded()
    {
        var usage = new StageRecorder();
        var middleware = Create(
            new RateLimitsConfigSection
            {
                Default = new(60, 0, 0),
                Models = Map(("gpt-4", new(1, 0, 0))),
                ApiKeyModels = Map(("key-1|gpt-4", new(50, 0, 0))),
            },
            usage);

        await Invoke(middleware);
        usage.Stages.Clear();
        (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);

        usage.Stages.Should().Equal(
            (RateLimitStageOutcome.Refused, "model:gpt-4,api_key_model:key-1|gpt-4", RateLimitKeys.Model("gpt-4")),
            (RateLimitStageOutcome.RefundedByLaterStage, "default", null));
    }

    [Fact]
    public async Task AnAdmittedRequest_ChargesBothStages()
    {
        var usage = new StageRecorder();
        var middleware = Create(
            new RateLimitsConfigSection
            {
                Default = new(60, 0, 0),
                ApiKeys = Map(("key-1", new(30, 0, 0))),
                Models = Map(("gpt-4", new(100, 0, 0))),
            },
            usage);

        await Invoke(middleware);

        usage.Stages.Should().Equal(
            (RateLimitStageOutcome.Charged, "model:gpt-4", null),
            (RateLimitStageOutcome.Charged, "default,api_key:key-1", null));
    }

    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    private static RateLimitMiddleware Create(RateLimitsConfigSection rateLimits, IRateLimitUsageTracker usage)
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel(Arg.Any<string>(), out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "gpt-4", Url = "http://backend:8000" };
                return true;
            });

        return new RateLimitMiddleware(
            _ => Task.CompletedTask,
            new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits })),
            new InMemoryDistributedRateLimitStore(),
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            registry,
            governor: null,
            usage,
            TimeProvider.System);
    }

    private static async Task<HttpResponse> Invoke(RateLimitMiddleware middleware)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"model":"gpt-4"}"""));
        context.Response.Body = new MemoryStream();
        context.Items[TenantContextKeys.HttpContextItemKey] = new TenantContext
        {
            TenantId = TenantId,
            ApiKeyId = "key-1",
            TenantSlug = "acme",
        };

        await middleware.InvokeAsync(context);
        return context.Response;
    }

    private static IReadOnlyDictionary<string, RateLimitPolicy> Map(params (string Key, RateLimitPolicy Policy)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Policy, StringComparer.OrdinalIgnoreCase);

    private sealed class StageRecorder : IRateLimitUsageTracker
    {
        public List<(RateLimitStageOutcome Outcome, string LimitIds, string? Refused)> Stages { get; } = [];

        public void RecordRateStage(ReadOnlySpan<RateLimitRule> rules, RateLimitStageOutcome outcome, string? refusedPartitionKey = null) =>
            Stages.Add((outcome, string.Join(',', rules.ToArray().Select(static r => r.LimitId)), refusedPartitionKey));

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
}
