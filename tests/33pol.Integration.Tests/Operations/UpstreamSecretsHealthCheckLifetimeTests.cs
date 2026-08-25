using Microsoft.Extensions.DependencyInjection;
using Pol33.Integration.Tests.Support;
using Pol33.Registry.Health;

namespace Pol33.Integration.Tests.Operations;

public sealed class UpstreamSecretsHealthCheckLifetimeTests
{
    /// <summary>
    /// The check records an error record only when the undecryptable count rises, and that memory
    /// lives in the instance. ASP.NET resolves a health check through
    /// <see cref="ActivatorUtilities.GetServiceOrCreateInstance{T}"/> per execution, so an
    /// unregistered check is rebuilt on every probe and forgets — turning one standing fault into
    /// one Critical record per probe, on an endpoint the console polls on a timer. A unit test that
    /// news the check up itself cannot see this; only the container can.
    /// </summary>
    [Fact]
    public async Task IsResolvedAsASingleton_SoOnceOnlyRecordingSurvivesBetweenProbes()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            "sk-33pol-integration-admin-key");
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        // Exactly how the health check runtime obtains the instance.
        var first = ActivatorUtilities.GetServiceOrCreateInstance<UpstreamSecretsHealthCheck>(factory.Services);
        var second = ActivatorUtilities.GetServiceOrCreateInstance<UpstreamSecretsHealthCheck>(factory.Services);

        second.Should().BeSameAs(first);
    }
}
