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

    /// <summary>
    /// The anonymous tier is carried onto the initial snapshot like every other section, so a
    /// database-less deployment — and the window before the first database load — enforces it.
    /// </summary>
    [Fact]
    public void InitialSnapshot_CarriesTheAnonymousTier()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:GatewayDb", string.Empty);
            builder.UseSetting("Gateway:OperatorConsole:Enabled", "false");
            builder.UseSetting("RateLimiting:Anonymous:Rpm", "9");
            builder.UseSetting("RateLimiting:Anonymous:Burst", "3");
            builder.UseSetting("RateLimiting:Anonymous:MaxConcurrentStreams", "1");
        });

        var snapshot = factory.Services.GetRequiredService<IGatewayConfigProvider>().Current.RateLimits;

        snapshot.Anonymous.Rpm.Should().Be(9);
        snapshot.Anonymous.Burst.Should().Be(3);
        snapshot.Anonymous.MaxConcurrentStreams.Should().Be(1);
        factory.Services.GetRequiredService<IRateLimitPolicyResolver>().ResolveAnonymous().Rpm.Should().Be(9);
    }
}
