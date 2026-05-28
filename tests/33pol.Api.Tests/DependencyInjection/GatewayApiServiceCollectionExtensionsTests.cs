using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Api.DependencyInjection;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;

namespace Pol33.Api.Tests.DependencyInjection;

public sealed class GatewayApiServiceCollectionExtensionsTests
{
    [Fact]
    public void AddGatewayApi_RegistersApiServiceTypes()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IModelRegistry>());
        services.AddSingleton(Substitute.For<IBackendHealthStore>());
        services.AddScoped(_ => Substitute.For<IModelGrantService>());
        services.AddSingleton(Substitute.For<IConfigReload>());
        services.AddSingleton(Substitute.For<IGatewayDrainState>());
        services.AddSingleton(Substitute.For<IAdminSummaryReader>());
        services.AddGatewayApi();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ModelsApiService>().Should().NotBeNull();
        provider.GetRequiredService<GatewayHealthService>().Should().NotBeNull();
        provider.GetRequiredService<GatewayReadinessService>().Should().NotBeNull();
        provider.GetRequiredService<GatewayStatsService>().Should().NotBeNull();
        provider.GetRequiredService<GatewayProcessClock>().Should().NotBeNull();
        provider.GetRequiredService<IErrorResponseWriter>().Should().BeOfType<OpenAiErrorResponseWriter>();
    }

    [Fact]
    public void AddGatewayApi_ConfiguresProviderModelsDiscoveryHttpClientTimeout()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IModelRegistry>());
        services.AddSingleton(Substitute.For<IBackendHealthStore>());
        services.AddScoped(_ => Substitute.For<IModelGrantService>());
        services.AddSingleton(Substitute.For<IConfigReload>());
        services.AddSingleton(Substitute.For<IGatewayDrainState>());
        services.AddSingleton(Substitute.For<IAdminSummaryReader>());
        services.AddGatewayApi();

        using var provider = services.BuildServiceProvider();
        var discoveryClient = provider.GetRequiredService<OpenAiCompatibleProviderModelsClient>();
        var httpClient = GetInjectedHttpClient(discoveryClient);

        httpClient.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    private static HttpClient GetInjectedHttpClient(OpenAiCompatibleProviderModelsClient client)
    {
        var field = typeof(OpenAiCompatibleProviderModelsClient).GetField(
            "http",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        return (HttpClient)field!.GetValue(client)!;
    }
}
