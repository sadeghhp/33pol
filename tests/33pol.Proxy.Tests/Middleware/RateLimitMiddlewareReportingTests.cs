using System.Text;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// What the limiter reports about itself: the numbers the usage report and the adaptive header are
/// built from.
/// </summary>
/// <remarks>
/// Both used to be derived from bucket <em>capacity</em>, which is <c>Rpm + Burst</c>. A report
/// comparing an observed per-minute rate against capacity understates utilisation by the entire
/// burst allowance — the column an operator reads to decide whether a tenant needs a bigger tier.
/// </remarks>
public sealed class RateLimitMiddlewareReportingTests
{
    [Fact]
    public async Task InvokeAsync_RecordsTheSustainedRate_NotTheBucketCapacity()
    {
        var usage = new RecordingUsageTracker();
        var middleware = Create(new RateLimitPolicy(60, 40, 0), usage: usage);

        await Invoke(middleware);

        var recorded = usage.Events.Should().ContainSingle().Subject;
        recorded.ConfiguredRpm.Should().Be(60);
        recorded.EffectiveRpm.Should().Be(60, "100 is the capacity, and utilisation is measured against the rate");
    }

    [Fact]
    public async Task InvokeAsync_OnARejection_RecordsTheSustainedRateToo()
    {
        var usage = new RecordingUsageTracker();
        var middleware = Create(new RateLimitPolicy(1, 0, 0), usage: usage);

        await Invoke(middleware);
        var refused = await Invoke(middleware);

        refused.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        usage.Events.Should().HaveCount(2);
        usage.Events[1].Admitted.Should().BeFalse();
        usage.Events[1].EffectiveRpm.Should().Be(1);
    }

    /// <summary>
    /// The adaptive header names the two rates the governor moved between. It used to reconstruct
    /// the configured one by dividing the capacity by the factor, which does not invert
    /// <see cref="RateLimitPolicy.Scale"/> — that rounds rpm and burst independently and floors both,
    /// so the header disagreed with the configuration by a few whenever either rounding went the
    /// other way.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_UnderAdaptation_ReportsTheConfiguredAndEffectiveRates()
    {
        var usage = new RecordingUsageTracker();
        var middleware = Create(
            new RateLimitPolicy(1000, 0, 0),
            usage: usage,
            modelTier: new RateLimitPolicy(75, 7, 0),
            governor: new StubGovernor(0.7));

        var response = await Invoke(middleware);

        // 75 rpm scaled by 0.7 is 52 (banker's rounding on 52.5) and the burst rounds separately
        // from 4.9 to 5, so capacity is 57. Reconstructing the configured rate from that capacity —
        // 57 / 0.7 — gives 81, and the header used to read "57/81": both numbers wrong, and neither
        // one a rate.
        response.Headers[GatewayHeaders.RateLimitAdaptive].ToString().Should().Be("52/75");
        response.Headers[GatewayHeaders.RateLimitLimit].ToString().Should().Be("57", "the header budget is still capacity");

        var recorded = usage.Events.Should().ContainSingle().Subject;
        recorded.ConfiguredRpm.Should().Be(75);
        recorded.EffectiveRpm.Should().Be(52);
    }

    [Fact]
    public async Task InvokeAsync_WithNoAdaptation_SendsNoAdaptiveHeader()
    {
        var middleware = Create(new RateLimitPolicy(600, 100, 0));

        var response = await Invoke(middleware);

        response.Headers.ContainsKey(GatewayHeaders.RateLimitAdaptive).Should().BeFalse();
    }

    private static RateLimitMiddleware Create(
        RateLimitPolicy tenantTier,
        IRateLimitUsageTracker? usage = null,
        RateLimitPolicy? modelTier = null,
        IAdaptiveRateLimitGovernor? governor = null)
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
            AdaptiveEnabled = governor is not null,
        };

        var snapshot = new GatewayConfigSnapshot { RateLimits = rateLimits };

        return new RateLimitMiddleware(
            _ => Task.CompletedTask,
            new RateLimitPlanResolver(new StubConfigProvider(snapshot), governor),
            new InMemoryDistributedRateLimitStore(),
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            registry,
            governor,
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

        await middleware.InvokeAsync(context);
        return context.Response;
    }

    private sealed class RecordingUsageTracker : IRateLimitUsageTracker
    {
        public List<RateLimitUsageEvent> Events { get; } = [];

        public void Record(in RateLimitUsageEvent usageEvent) => Events.Add(usageEvent);

        public RateLimitUsageReport BuildReport(int minutes, int take, DateTimeOffset now) =>
            throw new NotSupportedException();

        public void Reset() => Events.Clear();
    }

    private sealed class StubGovernor(double factor) : IAdaptiveRateLimitGovernor
    {
        public bool IsEnabled => true;

        public double GetModelFactor(string modelId) => factor;

        public int GetRetryAfterSeconds(string partitionKey, int baseRetryAfterSeconds, DateTimeOffset now) =>
            baseRetryAfterSeconds;

        public void RecordOutcome(string partitionKey, bool admitted, DateTimeOffset now)
        {
        }

        public void Evaluate(DateTimeOffset now)
        {
        }

        public AdaptiveRateLimitSnapshot Snapshot() => AdaptiveRateLimitSnapshot.Disabled;
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }
}
