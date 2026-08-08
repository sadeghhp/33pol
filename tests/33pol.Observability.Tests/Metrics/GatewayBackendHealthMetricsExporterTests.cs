using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Observability.Metrics;

namespace Pol33.Observability.Tests.Metrics;

public sealed class GatewayBackendHealthMetricsExporterTests
{
    [Fact]
    public void ObserveMeasurements_ReflectsBackendHealthPerModel()
    {
        var registry = new FakeModelRegistry(
        [
            new ModelConfig { Id = "healthy-model", Url = "http://localhost:1" },
            new ModelConfig { Id = "sick-model", Url = "http://localhost:2" },
        ]);
        var healthStore = new FakeBackendHealthStore();
        healthStore.SetHealthy("healthy-model", true);
        healthStore.SetHealthy("sick-model", false);

        var measurements = GatewayBackendHealthMetricsExporter
            .ObserveMeasurements(registry, healthStore)
            .ToList();

        measurements.Should().HaveCount(2);
        measurements.Should().Contain(m => m.Value == 1);
        measurements.Should().Contain(m => m.Value == 0);
    }

    [Fact]
    public async Task StartAndStopAsync_CompleteSuccessfully()
    {
        var exporter = new GatewayBackendHealthMetricsExporter(
            new FakeModelRegistry([]),
            new FakeBackendHealthStore());

        await exporter.StartAsync(CancellationToken.None);
        await exporter.StopAsync(CancellationToken.None);
    }

    private sealed class FakeModelRegistry(IReadOnlyList<ModelConfig> models) : IModelRegistry
    {
        public IReadOnlyList<ModelConfig> GetAllModels() => models;

        public bool TryGetModel(string name, out ModelConfig? model)
        {
            model = models.FirstOrDefault(m =>
                string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase) ||
                m.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));
            return model is not null;
        }

        public bool ModelExists(string name) => TryGetModel(name, out _);

        public string? GetBackendUrl(string name) =>
            TryGetModel(name, out var model) ? model!.Url : null;

        public Task LoadModelsAsync(string configPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeBackendHealthStore : IBackendHealthStore
    {
        private readonly Dictionary<string, bool> _healthy = new(StringComparer.OrdinalIgnoreCase);

        public void SetHealthy(string modelId, bool healthy) => _healthy[modelId] = healthy;

        public bool IsBackendHealthy(string modelId) =>
            _healthy.TryGetValue(modelId, out var healthy) && healthy;

        public BackendHealth? GetHealth(string modelId) => null;

        public IReadOnlyDictionary<string, BackendHealth> GetAllHealth() =>
            new Dictionary<string, BackendHealth>();

        public void SetHealth(BackendHealth health) => _healthy[health.ModelId] = health.IsHealthy;
    }
}
