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

        handler.RequestedPaths.Where(p => p == "/health").Should().HaveCount(2);
    }

    [Fact]
    public async Task CheckBackendAsync_Unhealthy_StoresUnhealthyState()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var healthStore = new BackendHealthStore(Options.Create(new GatewayOptions()));
        var service = CreateService(handler, healthStore);

        await service.CheckBackendAsync(new ModelConfig
        {
            Id = "model-a",
            Url = "http://backend:8000",
        });

        healthStore.IsBackendHealthy("model-a").Should().BeFalse();
        healthStore.GetHealth("model-a")!.Error.Should().Be("All probe endpoints failed");
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
        handler.RequestedPaths.Should().Equal("/health", "/api/tags");
    }

    [Fact]
    public async Task ProbeBackendAsync_FirstEndpointSucceeds_StopsWithoutCallingLaterPaths()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            _ => throw new InvalidOperationException("Should not probe /api/tags after /health succeeds"));
        var service = CreateService(handler);

        var (isHealthy, statusCode, error) = await service.ProbeBackendAsync("http://backend:8000");

        isHealthy.Should().BeTrue();
        statusCode.Should().Be(200);
        error.Should().BeNull();
        handler.RequestedPaths.Should().Equal("/health");
    }

    [Fact]
    public async Task ProbeBackendAsync_AllEndpointsFail_ReturnsUnhealthy()
    {
        var handler = new SequenceHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var service = CreateService(handler);

        var (isHealthy, _, error) = await service.ProbeBackendAsync("http://backend:8000");

        isHealthy.Should().BeFalse();
        error.Should().Be("All probe endpoints failed");
        handler.RequestedPaths.Should().Equal("/health", "/api/tags", "/");
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
    public void BuildProbeUri_TrimsAndCombinesPaths()
    {
        var uri = HealthCheckService.BuildProbeUri("http://backend:8000", "/api/tags");

        uri.ToString().Should().Be("http://backend:8000/api/tags");
    }

    private static HealthCheckService CreateService(
        HttpMessageHandler handler,
        IBackendHealthStore? healthStore = null,
        IModelRegistry? registry = null)
    {
        var modelRegistry = registry ?? Substitute.For<IModelRegistry>();
        var store = healthStore ?? new BackendHealthStore(Options.Create(new GatewayOptions()));
        return new HealthCheckService(
            modelRegistry,
            store,
            Options.Create(new GatewayOptions { HealthCheckIntervalSeconds = 30 }),
            NullLogger<HealthCheckService>.Instance,
            new HttpClient(handler));
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
