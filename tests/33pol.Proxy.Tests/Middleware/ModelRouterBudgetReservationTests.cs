using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Forwarding;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Resilience;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// A budget reservation is taken before forwarding and is only settled by the usage event the
/// request produces. Every path that does not produce one must hand the headroom back immediately —
/// relying on the TTL held budget hostage for the whole TTL on failures, and (when the TTL was
/// shorter than a request could live) freed it while the request was still running, which let
/// concurrent requests overshoot a hard-stop budget.
/// </summary>
public sealed class ModelRouterBudgetReservationTests
{
    [Theory]
    [InlineData(ForwarderError.RequestTimedOut)]          // header timeout
    [InlineData(ForwarderError.ResponseBodyCanceled)]     // body idle timeout
    [InlineData(ForwarderError.ResponseBodyDestination)]  // upstream body failure
    [InlineData(ForwarderError.RequestCanceled)]          // client disconnect
    [InlineData(ForwarderError.Request)]                  // upstream error
    public async Task InvokeAsync_TerminalFailurePath_ReleasesReservation(ForwarderError error)
    {
        var budget = CreateBudgetEnforcement();
        await RunAsync(budget, CreateForwarderReturning(error));

        budget.Received(1).ReleaseReservation(Arg.Any<string>());
    }

    /// <summary>
    /// An exception escaping the forwarder must not strand the reservation either.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_UnexpectedForwardingException_ReleasesReservation()
    {
        var budget = CreateBudgetEnforcement();
        var forwarder = Substitute.For<IInferenceHttpForwarder>();
        forwarder.SendAsync(
                Arg.Any<HttpContext>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<StreamingHttpTransformer>(),
                Arg.Any<bool>(),
                Arg.Any<InferenceForwardTimeouts>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<ForwarderError>>(_ => throw new InvalidOperationException("boom"));

        var act = async () => await RunAsync(budget, forwarder);

        await act.Should().ThrowAsync<InvalidOperationException>();
        budget.Received(1).ReleaseReservation(Arg.Any<string>());
    }

    /// <summary>
    /// On success with parseable usage the downstream persistence handler settles the reservation,
    /// so the router must NOT release it early — doing so would reopen the accounting gap between
    /// reservation and persisted spend that the ledger closes.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_SuccessWithUsage_DoesNotReleaseReservationItself()
    {
        var budget = CreateBudgetEnforcement();
        var usageRecorder = Substitute.For<IUsageRecorder>();

        // Accepted for persistence. Enqueue reports acceptance because a saturated queue drops
        // silently, and the router must only skip its own release when persistence will actually run.
        usageRecorder.Enqueue(Arg.Any<UsageEvent>()).Returns(true);

        await RunAsync(
            budget,
            CreateForwarderReturning(ForwarderError.None, enqueueUsage: true),
            usageRecorder);

        usageRecorder.Received(1).Enqueue(Arg.Any<UsageEvent>());
        budget.DidNotReceive().ReleaseReservation(Arg.Any<string>());
    }

    /// <summary>
    /// A 2xx whose body carried no parseable usage produces no usage event, so nothing downstream
    /// will ever settle the reservation. The router has to release it.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_SuccessWithoutParseableUsage_ReleasesReservation()
    {
        var budget = CreateBudgetEnforcement();

        await RunAsync(budget, CreateForwarderReturning(ForwarderError.None, enqueueUsage: false));

        budget.Received(1).ReleaseReservation(Arg.Any<string>());
    }

    /// <summary>
    /// The reservation, the usage event and the recent-request entry must all carry one id, or the
    /// release cannot match the reservation and the dashboard cannot be correlated with billing.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_UsesOneRequestIdAcrossReservationUsageAndRecentRequest()
    {
        var budget = CreateBudgetEnforcement();
        var usageRecorder = Substitute.For<IUsageRecorder>();

        RecentRequestEntry? recorded = null;
        var recentRequestStore = Substitute.For<IRecentRequestStore>();
        recentRequestStore.When(x => x.Record(Arg.Any<RecentRequestEntry>()))
            .Do(call => recorded = call.Arg<RecentRequestEntry>());

        string? reservedRequestId = null;
        budget.TryReserveAsync(
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                reservedRequestId = callInfo.ArgAt<string>(1);
                return BudgetCheckResult.Allowed;
            });

        // No RequestIdMiddleware has run, so the router must mint the id once and reuse it.
        await RunAsync(
            budget,
            CreateForwarderReturning(ForwarderError.None, enqueueUsage: true),
            usageRecorder,
            recentRequestStore);

        reservedRequestId.Should().NotBeNullOrEmpty();

        var usageEvent = (UsageEvent)usageRecorder.ReceivedCalls().Single().GetArguments()[0]!;
        usageEvent.RequestId.Should().Be(reservedRequestId);

        recorded.Should().NotBeNull();
        recorded!.RequestId.Should().Be(reservedRequestId);
    }

    /// <summary>Releasing twice must not corrupt ledger state — the router relies on idempotence.</summary>
    [Fact]
    public async Task InvokeAsync_RepeatedRequests_ReleaseIsIdempotentAcrossRuns()
    {
        var budget = CreateBudgetEnforcement();
        var forwarder = CreateForwarderReturning(ForwarderError.Request);

        await RunAsync(budget, forwarder);
        await RunAsync(budget, forwarder);

        budget.Received(2).ReleaseReservation(Arg.Any<string>());
    }

    /// <summary>A request rejected by the budget check itself never reserved, so nothing to release.</summary>
    [Fact]
    public async Task InvokeAsync_ReservationRejected_DoesNotRelease()
    {
        var budget = Substitute.For<IBudgetEnforcementService>();
        budget.CheckBeforeForwardAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.Allowed);
        budget.TryReserveAsync(
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.HardExceeded("monthly"));

        var forwarder = CreateForwarderReturning(ForwarderError.None);
        await RunAsync(budget, forwarder);

        budget.DidNotReceive().ReleaseReservation(Arg.Any<string>());
        await forwarder.DidNotReceive().SendAsync(
            Arg.Any<HttpContext>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<StreamingHttpTransformer>(),
            Arg.Any<bool>(),
            Arg.Any<InferenceForwardTimeouts>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A rejected reservation already counts as an error on the dashboard, so it has to leave a
    /// trace an operator can find. Without this the Overview counter climbs while the live feed and
    /// the Errors tab stay empty — a number with nothing behind it.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_BudgetRejected_RecordsTheFailureForTheFeedAndErrorsTab()
    {
        var budget = Substitute.For<IBudgetEnforcementService>();
        budget.CheckBeforeForwardAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.Allowed);
        budget.TryReserveAsync(
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.HardExceeded("monthly-cap"));

        RecentRequestEntry? recorded = null;
        var recentRequestStore = Substitute.For<IRecentRequestStore>();
        recentRequestStore.When(x => x.Record(Arg.Any<RecentRequestEntry>()))
            .Do(call => recorded = call.Arg<RecentRequestEntry>());

        var errors = new List<GatewayErrorRecord>();
        var errorRecorder = Substitute.For<IGatewayErrorRecorder>();
        errorRecorder.When(x => x.Record(Arg.Any<GatewayErrorRecord>()))
            .Do(call => errors.Add(call.Arg<GatewayErrorRecord>()));

        await RunAsync(
            budget,
            CreateForwarderReturning(ForwarderError.None),
            recentRequestStore: recentRequestStore,
            errorRecorder: errorRecorder);

        recorded.Should().NotBeNull();
        recorded!.ErrorCode.Should().NotBeNull();

        errors.Should().ContainSingle();
        errors[0].Outcome.Should().Be("budget_exceeded");
        errors[0].ModelId.Should().Be("m1");
        // A budget stop is the gateway working as configured, not a fault to page someone about.
        errors[0].Level.Should().Be(GatewayLogLevel.Warning.ToString());
    }

    private static IBudgetEnforcementService CreateBudgetEnforcement()
    {
        var budget = Substitute.For<IBudgetEnforcementService>();
        budget.CheckBeforeForwardAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.Allowed);
        budget.TryReserveAsync(
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.Allowed);
        return budget;
    }

    /// <summary>
    /// Stands in for the real forwarder. When <paramref name="enqueueUsage"/> is set it drives the
    /// transformer's usage capture with a well-formed body, which is what settles the reservation
    /// downstream in production.
    /// </summary>
    private static IInferenceHttpForwarder CreateForwarderReturning(
        ForwarderError error,
        bool enqueueUsage = false)
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
            .Returns(async callInfo =>
            {
                if (enqueueUsage)
                {
                    var transformer = callInfo.ArgAt<StreamingHttpTransformer>(3);
                    using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"usage":{"prompt_tokens":10,"completion_tokens":20}}""",
                            Encoding.UTF8,
                            "application/json"),
                    };

                    await transformer.TransformResponseAsync(
                        callInfo.ArgAt<HttpContext>(0), response, CancellationToken.None);

                    // Draining and disposing is what triggers capture in the real forwarder.
                    await using var body = await response.Content.ReadAsStreamAsync();
                    await body.CopyToAsync(Stream.Null);
                }

                return error;
            });

        return forwarder;
    }

    private static async Task RunAsync(
        IBudgetEnforcementService budgetEnforcement,
        IInferenceHttpForwarder forwarder,
        IUsageRecorder? usageRecorder = null,
        IRecentRequestStore? recentRequestStore = null,
        IGatewayErrorRecorder? errorRecorder = null)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-budget-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, """
            { "models": [ { "id": "m1", "url": "http://backend:8000", "aliases": [] } ] }
            """);

        try
        {
            var registry = new Pol33.Registry.Services.ModelRegistryService(
                NullLogger<Pol33.Registry.Services.ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(configPath);

            var middleware = CreateMiddleware(
                registry, forwarder, budgetEnforcement, usageRecorder, recentRequestStore, errorRecorder);

            var bodyBytes = Encoding.UTF8.GetBytes("""{"model":"m1","stream":false}""");
            var context = new DefaultHttpContext
            {
                Request =
                {
                    Method = HttpMethods.Post,
                    Path = "/v1/chat/completions",
                    Body = new MemoryStream(bodyBytes),
                    ContentType = "application/json",
                    ContentLength = bodyBytes.Length,
                },
                Response = { Body = new MemoryStream() },
            };

            await middleware.InvokeAsync(context);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private static ModelRouterMiddleware CreateMiddleware(
        IModelRegistry registry,
        IInferenceHttpForwarder forwarder,
        IBudgetEnforcementService budgetEnforcement,
        IUsageRecorder? usageRecorder,
        IRecentRequestStore? recentRequestStore,
        IGatewayErrorRecorder? errorRecorder = null)
    {
        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy(Arg.Any<string>()).Returns(true);

        var authState = Substitute.For<IGatewayAuthenticationState>();
        authState.IsAuthenticationRequired.Returns(false);

        var requestTracker = Substitute.For<IRequestTracker>();
        requestTracker.BeginInferenceRequest(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(_ => Substitute.For<IInferenceRequestScope>());

        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var options = Options.Create(new GatewayOptions());

        var rateLimitResolver = Substitute.For<IRateLimitPolicyResolver>();
        rateLimitResolver.Resolve(Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new RateLimitPolicy(10_000, 1_000, 1_000));
        rateLimitResolver.IsEnabled().Returns(true);

        var rateLimitStore = Substitute.For<IDistributedRateLimitStore>();
        rateLimitStore.TryAcquireStreamSlot(Arg.Any<string>(), Arg.Any<RateLimitPolicy>())
            .Returns(new RateLimitAcquireResult(true));

        return new ModelRouterMiddleware(
            _ => Task.CompletedTask,
            registry,
            health,
            Substitute.For<IServiceScopeFactory>(),
            authState,
            new OpenAiErrorResponseWriter(),
            requestTracker,
            recentRequestStore ?? Substitute.For<IRecentRequestStore>(),
            usageRecorder ?? Substitute.For<IUsageRecorder>(),
            metrics,
            new ModelCircuitBreakerRegistry(options, metrics),
            new BulkheadRegistry(options, metrics),
            rateLimitResolver,
            rateLimitStore,
            forwarder,
            options,
            Substitute.For<IUpstreamBearerTokenResolver>(),
            budgetEnforcement,
            errorRecorder ?? Substitute.For<IGatewayErrorRecorder>(),
            NullLogger<ModelRouterMiddleware>.Instance);
    }
}
