using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pol33.Security.Hosting;

namespace Pol33.Security.Tests.Hosting;

public sealed class GatewayAuthenticationInitializerTests
{
    [Fact]
    public async Task StartAsync_ProductionWithoutConnectionString_FailsClosed()
    {
        var provider = BuildProvider();
        var sut = new GatewayAuthenticationInitializer(
            provider,
            new FakeEnvironment("Production"),
            Substitute.For<ILogger<GatewayAuthenticationInitializer>>());

        var act = async () => await sut.StartAsync(CancellationToken.None);

        // Must abort startup rather than silently leaving authentication disabled.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_DevelopmentWithoutConnectionString_DisablesAuth()
    {
        var provider = BuildProvider();
        var state = provider.GetRequiredService<GatewayAuthenticationState>();
        var sut = new GatewayAuthenticationInitializer(
            provider,
            new FakeEnvironment("Development"),
            Substitute.For<ILogger<GatewayAuthenticationInitializer>>());

        await sut.StartAsync(CancellationToken.None);

        state.IsAuthenticationRequired.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ProductionWithExplicitAllowAnonymous_DisablesAuth()
    {
        var provider = BuildProvider(
            ("Gateway:Security:AllowAnonymous", "true"));
        var state = provider.GetRequiredService<GatewayAuthenticationState>();
        var sut = new GatewayAuthenticationInitializer(
            provider,
            new FakeEnvironment("Production"),
            Substitute.For<ILogger<GatewayAuthenticationInitializer>>());

        await sut.StartAsync(CancellationToken.None);

        state.IsAuthenticationRequired.Should().BeFalse();
    }

    private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
    {
        var services = new ServiceCollection();
        // Empty configuration => no GatewayDb connection string, plus any explicit overrides.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<GatewayAuthenticationState>();
        return services.BuildServiceProvider();
    }

    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
