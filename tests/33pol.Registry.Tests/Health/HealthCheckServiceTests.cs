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

        await service.CheckBackendAsync(new ModelConfig
        {
            Id = "model-a",
            Url = "http://backend:8000",
        });

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
        var service = CreateService(handler, healthStore, errorRecorder: recorder);
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
        var service = CreateService(handler, registry: registry, errorRecorder: recorder);

        registry.GetAllModels().Returns([model]);
        await service.CheckAllBackendsAsync();      // healthy -> unhealthy: one record

        registry.GetAllModels().Returns([]);
        await service.CheckAllBackendsAsync();      // model removed: the verdict is forgotten

        registry.GetAllModels().Returns([model]);
        await service.CheckAllBackendsAsync();      // re-added and down again: a fresh record

        recorder.Received(2).Record(Arg.Is<GatewayErrorRecord>(r => r.ModelId == "model-a"));
    }

    private static HealthCheckService CreateService(
        HttpMessageHandler handler,
        IBackendHealthStore? healthStore = null,
        IModelRegistry? registry = null,
        IUpstreamBearerTokenResolver? bearerTokenResolver = null,
        IGatewayErrorRecorder? errorRecorder = null)
    {
        var modelRegistry = registry ?? Substitute.For<IModelRegistry>();
        var store = healthStore ?? new BackendHealthStore(Options.Create(new GatewayOptions()));
        return new HealthCheckService(
            modelRegistry,
            store,
            bearerTokenResolver ?? Substitute.For<IUpstreamBearerTokenResolver>(),
            Options.Create(new GatewayOptions { HealthCheckIntervalSeconds = 30 }),
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
