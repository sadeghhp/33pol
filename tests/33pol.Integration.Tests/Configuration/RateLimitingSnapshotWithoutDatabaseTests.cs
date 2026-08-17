using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;

namespace Pol33.Integration.Tests.Configuration;

public sealed class RateLimitingSnapshotWithoutDatabaseTests
{
    /// <summary>
    /// Without a database the initial snapshot is the only snapshot. It used to copy every
    /// rate-limit field except <c>Enabled</c>, so <c>RateLimiting:Enabled=false</c> was silently
    /// ignored and limits were enforced anyway.
    /// </summary>
    [Fact]
    public void InitialSnapshot_HonoursRateLimitingEnabledFalse()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:GatewayDb", string.Empty);
            builder.UseSetting("Gateway:OperatorConsole:Enabled", "false");
            builder.UseSetting("RateLimiting:Enabled", "false");
            builder.UseSetting("RateLimiting:Default:Rpm", "7");
        });

        var snapshot = factory.Services.GetRequiredService<IGatewayConfigProvider>().Current.RateLimits;

        snapshot.Enabled.Should().BeFalse();
        snapshot.Default.Rpm.Should().Be(7);
        factory.Services.GetRequiredService<IRateLimitPolicyResolver>().IsEnabled().Should().BeFalse();
    }
}
