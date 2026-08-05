using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Observability.Usage;

namespace Pol33.Integration.Tests.Support;

/// <summary>
/// Runtime counterpart to the static scan in <c>Pol33.Architecture.Tests</c>: resolves against the
/// real application container and proves the concrete type, the interface, and the hosted-service
/// registration are one object. Regression cover for the duplicate
/// <see cref="BillingUsageBatchPersistenceHandler"/> singleton, which left the instance receiving
/// usage events unstarted (no periodic flush, no shutdown drain).
/// </summary>
public sealed class SingletonInstanceIdentityTests
{
    [Fact]
    public void BillingUsageBatchPersistenceHandler_ResolvesToASingleInstance()
    {
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();

        var concrete = factory.Services.GetRequiredService<BillingUsageBatchPersistenceHandler>();
        var asInterface = factory.Services.GetRequiredService<IUsagePersistenceHandler>();
        var asHostedService = factory.Services.GetServices<IHostedService>()
            .OfType<BillingUsageBatchPersistenceHandler>()
            .Single();

        asInterface.Should().BeSameAs(concrete);
        asHostedService.Should().BeSameAs(concrete);
    }

    [Fact]
    public void ChannelUsageRecorder_ResolvesToASingleInstance()
    {
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();

        var concrete = factory.Services.GetRequiredService<ChannelUsageRecorder>();
        var asInterface = factory.Services.GetRequiredService<IUsageRecorder>();
        var asHostedService = factory.Services.GetServices<IHostedService>()
            .OfType<ChannelUsageRecorder>()
            .Single();

        asInterface.Should().BeSameAs(concrete);
        asHostedService.Should().BeSameAs(concrete);
    }

    /// <summary>
    /// Broad sweep: for every started hosted service, any other singleton service type that yields
    /// an object of the same concrete type must yield that same object.
    /// </summary>
    [Fact]
    public void EveryHostedService_SharesItsInstanceWithOtherServiceTypes()
    {
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        var provider = factory.Services;

        var mismatches = new List<string>();

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            var implementationType = hosted.GetType();

            // Only concrete self-registrations are checked here; interface aliases are covered by
            // the two explicit tests above and by the static descriptor scan.
            var selfRegistered = provider.GetService(implementationType);
            if (selfRegistered is not null && !ReferenceEquals(selfRegistered, hosted))
            {
                mismatches.Add(
                    $"{implementationType.Name} resolved by its own type is a different instance " +
                    "from the one registered as IHostedService.");
            }
        }

        mismatches.Should().BeEmpty(string.Join(Environment.NewLine, mismatches));
    }
}
