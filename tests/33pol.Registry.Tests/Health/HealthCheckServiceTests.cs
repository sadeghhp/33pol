using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Registry.Health;

namespace Pol33.Registry.Tests.Health;

public sealed class HealthCheckServiceTests
{
    [Fact]
    public async Task CheckAllBackendsAsync_EmptyRegistry_DoesNotProbe()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns([]);
        var service = CreateService(handler, registry: registry);

        await service.CheckAllBackendsAsync();

        handler.RequestedPaths.Should().BeEmpty();
    }

    /// <summary>
    /// The store used to keep the last status of deleted or renamed models forever: stale rows in the
    /// backends view, strict-mode answers for ids that no longer exist, unbounded growth over
    /// add/rename/delete cycles. Each sweep now prunes to the current registry.
    /// </summary>
    [Fact]
    public async Task CheckAllBackendsAsync_ForgetsModelsRemovedFromTheRegistry()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        healthStore.SetHealth(new BackendHealth("gone", "http://gone:8000", false, 503, "old", DateTimeOffset.UtcNow));
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns([new ModelConfig { Id = "a", Url = "http://a:8000" }]);
        var service = CreateService(handler, healthStore, registry);

        await service.CheckAllBackendsAsync();

        healthStore.GetAllHealth().Keys.Should().BeEquivalentTo("a");

        registry.GetAllModels().Returns([]);
        await service.CheckAllBackendsAsync();

        healthStore.GetAllHealth().Should().BeEmpty();
    }

    /// <summary>
    /// A stopped route is not probed. Probing one would spend a connection per sweep on a backend
    /// the gateway will not forward to, and — because the sweep records faults — would raise errors
    /// and attention items for a model an operator deliberately took out of service.
    /// </summary>
    [Fact]
    public async Task CheckAllBackendsAsync_SkipsStoppedModels()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "serving", Url = "http://serving:8000" },
            new ModelConfig { Id = "stopped", Url = "http://stopped:8000", State = ModelRouteStates.Stopped },
        ]);
        var service = CreateService(handler, registry: registry);

        await service.CheckAllBackendsAsync();

        handler.RequestedPaths.Where(p => p == "/v1/models").Should().HaveCount(1);
    }

    /// <summary>
    /// Stopping a model clears its health row too, so the backends view does not keep showing a
    /// verdict from the last sweep that ran while it was still serving.
    /// </summary>
    [Fact]
    public async Task CheckAllBackendsAsync_StoppingAModel_ForgetsItsLastHealthVerdict()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns([new ModelConfig { Id = "a", Url = "http://a:8000" }]);
        var service = CreateService(handler, healthStore, registry);

        await service.CheckAllBackendsAsync();
        healthStore.GetAllHealth().Keys.Should().BeEquivalentTo("a");

        registry.GetAllModels().Returns(
            [new ModelConfig { Id = "a", Url = "http://a:8000", State = ModelRouteStates.Stopped }]);
        await service.CheckAllBackendsAsync();

        healthStore.GetAllHealth().Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAllBackendsAsync_WithModels_ProbesEachBackend()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "a", Url = "http://a:8000" },
            new ModelConfig { Id = "b", Url = "http://b:8000" },
        ]);
        var service = CreateService(handler, registry: registry);

        await service.CheckAllBackendsAsync();

        handler.RequestedPaths.Where(p => p == "/v1/models").Should().HaveCount(2);
    }

    [Fact]
    public async Task CheckBackendAsync_Unhealthy_StoresUnhealthyState()
    {
        var handler = SequenceHttpMessageHandler.AlwaysReturning(HttpStatusCode.ServiceUnavailable);
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        var service = CreateService(handler, healthStore);
        var model = new ModelConfig { Id = "model-a", Url = "http://backend:8000" };

        // Takes HealthCheckUnhealthyThreshold consecutive failed sweeps, not one.
        await service.CheckBackendAsync(model);
        await service.CheckBackendAsync(model);

        healthStore.IsBackendHealthy("model-a").Should().BeFalse();
        healthStore.GetHealth("model-a")!.Error.Should().Contain("503");
        healthStore.GetHealth("model-a")!.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task ProbeBackendAsync_RequestExceptionThenSuccess_UsesFallbackPath()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => throw new HttpRequestException("connection refused"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler);

        var (isHealthy, statusCode, error) = await service.ProbeBackendAsync("http://backend:8000");

        isHealthy.Should().BeTrue();
        statusCode.Should().Be(200);
        error.Should().BeNull();
        handler.RequestedPaths.Should().Equal("/v1/models", "/health");
    }

    [Fact]
    public async Task ProbeBackendAsync_FirstEndpointSucceeds_StopsWithoutCallingLaterPaths()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            _ => throw new InvalidOperationException("Should not probe further after /v1/models succeeds"));
        var service = CreateService(handler);

        var (isHealthy, statusCode, error) = await service.ProbeBackendAsync("http://backend:8000");

        isHealthy.Should().BeTrue();
        statusCode.Should().Be(200);
        error.Should().BeNull();
        handler.RequestedPaths.Should().Equal("/v1/models");
    }

    [Fact]
    public async Task ProbeBackendAsync_AllEndpointsFail_ReturnsUnhealthy()
    {
        var handler = SequenceHttpMessageHandler.AlwaysReturning(HttpStatusCode.ServiceUnavailable);
        var service = CreateService(handler);

        var (isHealthy, _, error) = await service.ProbeBackendAsync("http://backend:8000");

        isHealthy.Should().BeFalse();
        error.Should().Contain("503");
        handler.RequestedPaths.Should().Equal("/v1/models", "/health", "/api/tags", "/");
    }

    [Fact]
    public async Task CheckBackendAsync_ProbeSucceeds_StoresHealthyState()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        var service = CreateService(handler, healthStore);

        await service.CheckBackendAsync(new ModelConfig
        {
            Id = "model-a",
            Url = "http://backend:8000",
        });

        healthStore.IsBackendHealthy("model-a").Should().BeTrue();
        healthStore.GetHealth("model-a")!.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task ProbeBackendAsync_Unauthorized_TreatsBackendAsReachable()
    {
        var handler = SequenceHttpMessageHandler.AlwaysReturning(HttpStatusCode.Unauthorized);
        var service = CreateService(handler);

        var (isHealthy, statusCode, error) = await service.ProbeBackendAsync("http://backend:8000", "sk-test");

        isHealthy.Should().BeTrue();
        statusCode.Should().Be(401);
        error.Should().Contain("credential");
        handler.RequestedPaths.Should().Equal("/v1/models");
    }

    [Fact]
    public async Task ProbeBackendAsync_InvalidUrl_ReturnsUnhealthyWithoutThrowing()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler);

        var (isHealthy, statusCode, error) = await service.ProbeBackendAsync("not a url");

        isHealthy.Should().BeFalse();
        statusCode.Should().BeNull();
        error.Should().Contain("Invalid backend URL");
    }

    [Fact]
    public async Task CheckBackendAsync_BearerResolverThrows_StillProbesWithoutCredential()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var resolver = Substitute.For<IUpstreamBearerTokenResolver>();
        resolver.When(r => r.ResolveBearerToken(Arg.Any<UpstreamAuthConfig?>()))
            .Do(_ => throw new InvalidOperationException("resolver failed"));
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        var service = CreateService(handler, healthStore, bearerTokenResolver: resolver);

        await service.CheckBackendAsync(new ModelConfig
        {
            Id = "model-a",
            Url = "http://backend:8000",
            UpstreamAuth = new UpstreamAuthConfig { Type = "bearer", EnvVar = "OPENAI_API_KEY" },
        });

        healthStore.IsBackendHealthy("model-a").Should().BeTrue();
    }

    [Fact]
    public void BuildProbeUri_TrimsAndCombinesPaths()
    {
        var uri = HealthCheckService.BuildProbeUri("http://backend:8000", "/api/tags");

        uri.ToString().Should().Be("http://backend:8000/api/tags");
    }

    /// <summary>
    /// An outage is one fault however many sweeps observe it: the Errors tab gets one record when
    /// the backend goes down, none while it stays down, and a fresh one if it goes down again.
    /// </summary>
    [Fact]
    public async Task CheckBackendAsync_RecordsAnErrorOncePerUnhealthyTransition()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"),
            _ => throw new HttpRequestException("connection refused"));
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        var recorder = Substitute.For<IGatewayErrorRecorder>();
        // Threshold 1: this test is about recording a transition once, not about the hysteresis,
        // and the handler's fixed response sequence maps one sweep to one verdict.
        var service = CreateService(handler, healthStore, errorRecorder: recorder, unhealthyThreshold: 1);
        var model = new ModelConfig { Id = "model-a", Url = "http://backend:8000" };

        await service.CheckBackendAsync(model);   // healthy -> unhealthy (4 probe paths fail)
        await service.CheckBackendAsync(model);   // still unhealthy
        await service.CheckBackendAsync(model);   // recovers on the first path
        await service.CheckBackendAsync(model);   // unhealthy again

        recorder.Received(2).Record(Arg.Is<GatewayErrorRecord>(r =>
            r.Source == GatewayErrorSourceNames.Health
            && r.ModelId == "model-a"
            && r.Outcome == "backend_unhealthy"
            && r.Message.Contains("connection refused")));
    }

    /// <summary>
    /// The service remembers each backend's last verdict so a standing outage records once. That
    /// memory has to be forgotten when a model leaves the registry — otherwise a model removed
    /// while unhealthy and later re-added is still remembered as down, and its next outage is
    /// silently never recorded.
    /// </summary>
    [Fact]
    public async Task CheckAllBackendsAsync_ForgetsTheLastVerdictOfRemovedModels()
    {
        var handler = SequenceHttpMessageHandler.AlwaysReturning(HttpStatusCode.ServiceUnavailable);
        var registry = Substitute.For<IModelRegistry>();
        var model = new ModelConfig { Id = "model-a", Url = "http://backend:8000" };
        var recorder = Substitute.For<IGatewayErrorRecorder>();
        // Threshold 1: this test is about forgetting a removed model's verdict, not the hysteresis.
        var service = CreateService(handler, registry: registry, errorRecorder: recorder, unhealthyThreshold: 1);

        registry.GetAllModels().Returns([model]);
        await service.CheckAllBackendsAsync();      // healthy -> unhealthy: one record

        registry.GetAllModels().Returns([]);
        await service.CheckAllBackendsAsync();      // model removed: the verdict is forgotten

        registry.GetAllModels().Returns([model]);
        await service.CheckAllBackendsAsync();      // re-added and down again: a fresh record

        recorder.Received(2).Record(Arg.Is<GatewayErrorRecord>(r => r.ModelId == "model-a"));
    }

    /// <summary>
    /// A single failed sweep must not take a backend out of service.
    /// </summary>
    /// <remarks>
    /// The defect this covers: one failed probe marked the backend unhealthy, and the router refuses
    /// every request to an unhealthy backend outright — so one slow sweep was a full outage for that
    /// model until the next successful one. The probe endpoint is served by the same process that is
    /// generating, so a saturated model server answers slowly exactly when it is busiest: the probe
    /// failed <em>because</em> the model was loaded, and the gateway then refused all its traffic.
    /// </remarks>
    [Fact]
    public async Task CheckBackendAsync_SingleFailedProbe_KeepsBackendServing()
    {
        var handler = SequenceHttpMessageHandler.AlwaysReturning(HttpStatusCode.ServiceUnavailable);
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        var service = CreateService(handler, healthStore, unhealthyThreshold: 2);

        await service.CheckBackendAsync(new ModelConfig { Id = "model-a", Url = "http://backend:8000" });

        healthStore.IsBackendHealthy("model-a").Should().BeTrue("one bad sweep is not an outage");

        // The observed failure is still visible, so a degradation shows on the Backends card as it
        // builds rather than appearing only once the backend is already refused.
        healthStore.GetHealth("model-a")!.StatusCode.Should().Be(503);
        healthStore.GetHealth("model-a")!.Error.Should().Contain("503");
    }

    [Fact]
    public async Task CheckBackendAsync_ConsecutiveFailuresReachThreshold_MarksBackendUnhealthy()
    {
        var handler = SequenceHttpMessageHandler.AlwaysReturning(HttpStatusCode.ServiceUnavailable);
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        var service = CreateService(handler, healthStore, unhealthyThreshold: 3);
        var model = new ModelConfig { Id = "model-a", Url = "http://backend:8000" };

        await service.CheckBackendAsync(model);
        await service.CheckBackendAsync(model);
        healthStore.IsBackendHealthy("model-a").Should().BeTrue("still one short of the threshold");

        await service.CheckBackendAsync(model);
        healthStore.IsBackendHealthy("model-a").Should().BeFalse("a sustained outage is a real one");
    }

    /// <summary>
    /// Slow to condemn, fast to forgive: one good probe restores service and resets the streak, so a
    /// recovered backend is not held out for another threshold's worth of sweeps.
    /// </summary>
    [Fact]
    public async Task CheckBackendAsync_SuccessAfterFailures_RestoresServiceAndResetsTheStreak()
    {
        var handler = new SequenceHttpMessageHandler(
            // Sweep 1: every path fails.
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            // Sweep 2: recovers on the first path.
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            // Sweep 3: fails again, but the streak restarted at sweep 2.
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        var service = CreateService(handler, healthStore, unhealthyThreshold: 2);
        var model = new ModelConfig { Id = "model-a", Url = "http://backend:8000" };

        await service.CheckBackendAsync(model);
        await service.CheckBackendAsync(model);
        healthStore.IsBackendHealthy("model-a").Should().BeTrue("the second sweep succeeded");

        await service.CheckBackendAsync(model);
        healthStore.IsBackendHealthy("model-a").Should().BeTrue(
            "the success reset the streak, so this is failure one of two again");
    }

    /// <summary>
    /// The path that answered is tried first next time, so a backend without <c>/v1/models</c> does
    /// not pay the full walk down the probe list — at the probe timeout per miss — on every sweep.
    /// </summary>
    [Fact]
    public async Task CheckBackendAsync_RemembersTheProbePathThatAnswered()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),   // /v1/models
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),   // /health
            _ => new HttpResponseMessage(HttpStatusCode.OK),         // /api/tags
            _ => new HttpResponseMessage(HttpStatusCode.OK));        // second sweep, first attempt
        var service = CreateService(handler);
        var model = new ModelConfig { Id = "model-a", Url = "http://backend:8000" };

        await service.CheckBackendAsync(model);
        handler.RequestedPaths.Should().Equal("/v1/models", "/health", "/api/tags");

        await service.CheckBackendAsync(model);

        handler.RequestedPaths.Skip(3).Should().Equal(
            ["/api/tags"],
            "the second sweep starts from the path that answered, not from the top of the list");
    }

    /// <param name="unhealthyThreshold">
    /// Consecutive failed probes before a backend is taken out of service. Left at the shipped
    /// default unless a test is specifically isolating behaviour that predates the hysteresis, in
    /// which case 1 keeps that test about the one thing it is checking.
    /// </param>
    private static HealthCheckService CreateService(
        HttpMessageHandler handler,
        IBackendHealthStore? healthStore = null,
        IModelRegistry? registry = null,
        IUpstreamBearerTokenResolver? bearerTokenResolver = null,
        IGatewayErrorRecorder? errorRecorder = null,
        int? unhealthyThreshold = null)
    {
        var modelRegistry = registry ?? Substitute.For<IModelRegistry>();
        var store = healthStore ?? new BackendHealthStore(Options.Create(new GatewayOptions()));
        var options = new GatewayOptions { HealthCheckIntervalSeconds = 30 };
        if (unhealthyThreshold is int threshold)
        {
            options.HealthCheckUnhealthyThreshold = threshold;
        }

        return new HealthCheckService(
            modelRegistry,
            store,
            bearerTokenResolver ?? Substitute.For<IUpstreamBearerTokenResolver>(),
            Options.Create(options),
            NullLogger<HealthCheckService>.Instance,
            new HttpClient(handler),
            errorRecorder);
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _responses;
        private int _index;
        public List<string> RequestedPaths { get; } = [];

        public SequenceHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = responses;
        }

        /// <summary>Answers every probe path with the same status, rather than falling back to 404.</summary>
        public static SequenceHttpMessageHandler AlwaysReturning(HttpStatusCode statusCode) =>
            new(Enumerable.Repeat<Func<HttpRequestMessage, HttpResponseMessage>>(
                _ => new HttpResponseMessage(statusCode), 8).ToArray());

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedPaths.Add(request.RequestUri!.AbsolutePath);
            var response = _index < _responses.Length
                ? _responses[_index++](request)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
