using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pol33.Billing.DependencyInjection;
using Pol33.Billing.Reconciliation;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Persistence.DependencyInjection;

namespace Pol33.Billing.Tests.DependencyInjection;

public sealed class BillingPersistenceServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGatewayBillingPersistence_WithoutConnectionString_DoesNotRegisterBatchHandler()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddGatewayBilling(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>()
            .Should()
            .NotContain(s => s is BillingUsageBatchPersistenceHandler);
    }

    [Fact]
    public void AddGatewayBillingPersistence_WithInMemoryDb_RegistersPersistenceServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GatewayDb"] = "InMemory:billing-di-test",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayPersistence(configuration);
        services.AddGatewayBilling(configuration);
        services.AddGatewayBillingPersistence(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IBillingUsageService>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IBudgetEnforcementService>().Should().NotBeOfType<NoOpBudgetEnforcementService>();
        provider.GetServices<IHostedService>()
            .Should()
            .Contain(s => s is BillingUsageBatchPersistenceHandler);
    }

    /// <summary>
    /// Reconciliation is the only thing that makes divergence between the ledger and the rollups
    /// visible, so a wiring mistake that quietly leaves it unregistered restores exactly the silent
    /// failure it was built to remove — with the added cost that the metric simply never appears
    /// rather than reporting a problem.
    /// </summary>
    [Fact]
    public void AddGatewayBillingPersistence_WithInMemoryDb_RegistersReconciliation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GatewayDb"] = "InMemory:billing-reconciliation-di-test",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayPersistence(configuration);
        services.AddGatewayBilling(configuration);
        services.AddGatewayBillingPersistence(configuration);

        // Deliberately no observability module: billing must compose on its own. Enumerating hosted
        // services is what constructs them, so a required cross-module dependency fails right here.
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IBillingReconciliationService>().Should().NotBeNull();
        provider.GetServices<IHostedService>()
            .Should()
            .Contain(s => s is BillingReconciliationHostedService);
    }

    /// <summary>Without persistence there is no ledger and no rollups, so there is nothing to compare.</summary>
    [Fact]
    public void AddGatewayBillingPersistence_WithoutConnectionString_DoesNotRegisterReconciliation()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddGatewayBilling(configuration);

        using var provider = services.BuildServiceProvider();

        provider.GetService<IBillingReconciliationService>().Should().BeNull();
        provider.GetServices<IHostedService>()
            .Should()
            .NotContain(s => s is BillingReconciliationHostedService);
    }

    /// <summary>
    /// The concrete type, <see cref="IUsagePersistenceHandler"/>, and the hosted-service registration
    /// must all be the same object. Registering the interface by implementation type built a second
    /// singleton: the started copy flushed an empty buffer while the copy receiving usage events was
    /// never started.
    /// </summary>
    [Fact]
    public void AddGatewayBillingPersistence_RegistersBatchHandlerAsASingleInstance()
    {
        using var provider = BuildProvider().Provider;

        var concrete = provider.GetRequiredService<BillingUsageBatchPersistenceHandler>();
        var asInterface = provider.GetRequiredService<IUsagePersistenceHandler>();
        var asHostedService = provider.GetServices<IHostedService>()
            .OfType<BillingUsageBatchPersistenceHandler>()
            .Single();

        asInterface.Should().BeSameAs(concrete);
        asHostedService.Should().BeSameAs(concrete);
    }

    /// <summary>
    /// A batch smaller than UsageWriterBatchSize must reach the repository on the periodic timer,
    /// without waiting for shutdown. With two instances this never happened: the timer ran on the
    /// instance whose buffer was always empty.
    /// </summary>
    [Fact]
    public async Task ResolvedHandler_PeriodicFlush_PersistsPartialBatchWithoutShutdown()
    {
        var (provider, billingEvents) = BuildProvider(batchSize: 100, flushIntervalMs: 40);
        using var _ = provider;

        await StartHostedServicesAsync(provider);
        try
        {
            var handler = provider.GetRequiredService<IUsagePersistenceHandler>();
            await handler.PersistAsync(CreateEvent("req-periodic"));

            await WaitForAsync(() => billingEvents.ReceivedAppends >= 1, TimeSpan.FromSeconds(5));

            billingEvents.ReceivedAppends.Should().Be(1);
            billingEvents.AppendedRequestIds.Should().Contain("req-periodic");
        }
        finally
        {
            await StopHostedServicesAsync(provider);
        }
    }

    /// <summary>
    /// Shutdown must drain whatever is still buffered. With two instances StopAsync drained the
    /// empty copy, so up to UsageWriterBatchSize-1 billing events were lost on every restart.
    /// </summary>
    [Fact]
    public async Task ResolvedHandler_Shutdown_PersistsPartialBatch()
    {
        var (provider, billingEvents) = BuildProvider(batchSize: 100, flushIntervalMs: 60_000);
        using var _ = provider;

        await StartHostedServicesAsync(provider);

        var handler = provider.GetRequiredService<IUsagePersistenceHandler>();
        await handler.PersistAsync(CreateEvent("req-shutdown"));

        billingEvents.ReceivedAppends.Should().Be(0, "the batch is below the flush threshold");

        await StopHostedServicesAsync(provider);

        billingEvents.ReceivedAppends.Should().Be(1);
        billingEvents.AppendedRequestIds.Should().Contain("req-shutdown");
    }

    private static (ServiceProvider Provider, RecordingBillingEventRepository BillingEvents) BuildProvider(
        int batchSize = 100,
        int flushIntervalMs = 1000)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GatewayDb"] = $"InMemory:billing-flush-{Guid.NewGuid():N}",
                ["Billing:UsageWriterBatchSize"] = batchSize.ToString(),
                ["Billing:UsageWriterFlushIntervalMs"] = flushIntervalMs.ToString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayPersistence(configuration);
        services.AddGatewayBilling(configuration);
        services.AddGatewayBillingPersistence(configuration);

        // Supplied by the Security module in the composed app; stubbed here so this test exercises
        // only the billing registration graph.
        services.TryAddSingleton(Substitute.For<IApiKeyLastUsedTracker>());

        var billingEvents = new RecordingBillingEventRepository();
        services.RemoveAll<IBillingEventRepository>();
        services.AddSingleton<IBillingEventRepository>(billingEvents);

        return (services.BuildServiceProvider(), billingEvents);
    }

    private static async Task StartHostedServicesAsync(IServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>()
                     .OfType<BillingUsageBatchPersistenceHandler>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    private static async Task StopHostedServicesAsync(IServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>()
                     .OfType<BillingUsageBatchPersistenceHandler>())
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static UsageEvent CreateEvent(string requestId) =>
        new()
        {
            RequestId = requestId,
            ModelId = "gpt-4o",
            PromptTokens = 3,
            CompletionTokens = 5,
            DurationMs = 12,
        };

    private sealed class RecordingBillingEventRepository : IBillingEventRepository
    {
        private readonly List<string> _appended = [];
        private readonly object _sync = new();

        public int ReceivedAppends
        {
            get { lock (_sync) { return _appended.Count; } }
        }

        public IReadOnlyList<string> AppendedRequestIds
        {
            get { lock (_sync) { return [.. _appended]; } }
        }

        public Task<bool> TryAppendAsync(BillingEventRecord record, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _appended.Add(record.RequestId);
            }

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<BillingEventRecord>> QueryAsync(
            BillingEventQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BillingEventRecord>>([]);

        public Task<IReadOnlyDictionary<Guid, ApiKeyUsageSummary>> GetUsageSummariesAsync(
            Guid tenantId,
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ApiKeyUsageSummary>>(
                new Dictionary<Guid, ApiKeyUsageSummary>());

        public Task<IReadOnlyList<DailyUsageRollupRecord>> GetDailyTotalsAsync(
            DateOnly fromDate,
            DateOnly toDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailyUsageRollupRecord>>([]);

        public Task<IReadOnlyList<DailyUsageRollupRecord>> AggregateDailyAsync(
            BillingEventQuery filter,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DailyUsageRollupRecord>>([]);
    }
}
