using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pol33.App;

namespace Pol33.Architecture.Tests;

/// <summary>
/// Guards the singleton-aliasing rule: a concrete type registered under more than one service type
/// (typically its own type, an interface, and <c>IHostedService</c>) must be aliased with a factory
/// so every service type resolves the SAME object.
///
/// Registering the second service type by implementation type —
/// <c>ServiceDescriptor.Singleton&lt;IFoo, Foo&gt;()</c> — silently constructs an independent
/// instance. That produced a real defect: the hosted-service copy of
/// <c>BillingUsageBatchPersistenceHandler</c> ran its flush loop over an empty buffer while the copy
/// resolved as <c>IUsagePersistenceHandler</c> received every usage event and was never started, so
/// usage below the batch threshold was never flushed and pending events were lost at shutdown.
///
/// This scan is static: it inspects descriptors and never builds a provider, so it covers the whole
/// registration graph without needing a resolvable one. The matching runtime assertions live in
/// <c>Pol33.Integration.Tests.Support.SingletonInstanceIdentityTests</c>.
/// </summary>
public sealed class SingletonInstanceIdentityTests
{
    [Fact]
    public void GatewayRegistrations_DoNotDuplicateSingletonImplementations()
    {
        var services = BuildFullGatewayServices();

        var duplicated = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType is not null)
            .GroupBy(d => d.ImplementationType!)
            .Where(g => g.Count() > 1)
            .Select(g =>
                $"{g.Key.FullName} is used as the implementation type of {g.Count()} singleton " +
                $"descriptors ({string.Join(", ", g.Select(d => d.ServiceType.Name))}), which builds " +
                "one instance per descriptor. Alias the extra service types with " +
                "sp => sp.GetRequiredService<T>() instead.")
            .ToList();

        duplicated.Should().BeEmpty(string.Join(Environment.NewLine, duplicated));
    }

    /// <summary>
    /// A type registered as a hosted service must not also be registered elsewhere by implementation
    /// type: only one of the two instances would have Start/StopAsync called on it.
    /// </summary>
    [Fact]
    public void HostedServiceImplementations_AreNotAlsoRegisteredByImplementationType()
    {
        var services = BuildFullGatewayServices();

        var hostedImplementationTypes = services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType is not null)
            .Select(d => d.ImplementationType!)
            .ToHashSet();

        var offenders = services
            .Where(d => d.ServiceType != typeof(IHostedService) &&
                        d.ImplementationType is not null &&
                        hostedImplementationTypes.Contains(d.ImplementationType))
            .Select(d =>
                $"{d.ImplementationType!.Name} is registered as IHostedService by implementation type " +
                $"and again as {d.ServiceType.Name} by implementation type; these resolve to different " +
                "instances, so the started instance is not the one the application uses.")
            .ToList();

        offenders.Should().BeEmpty(string.Join(Environment.NewLine, offenders));
    }

    private static IServiceCollection BuildFullGatewayServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GatewayDb"] = "InMemory:singleton-identity-test",
                ["Gateway:OperatorConsole:Enabled"] = "false",
            })
            .Build();

        var environment = new HostingEnvironment
        {
            EnvironmentName = Environments.Development,
            ApplicationName = "33pol.App",
            ContentRootPath = AppContext.BaseDirectory,
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayCore(configuration, environment);
        return services;
    }

    private sealed class HostingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = string.Empty;

        public string ContentRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
