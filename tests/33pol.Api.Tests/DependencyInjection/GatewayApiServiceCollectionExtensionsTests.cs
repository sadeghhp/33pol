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
        services.AddSingleton(Substitute.For<IConfigReload>());
        services.AddSingleton(Substitute.For<IGatewayDrainState>());
        services.AddSingleton(Substitute.For<IAdminSummaryReader>());
        services.AddGatewayApi();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ModelsApiService>().Should().NotBeNull();
        provider.GetRequiredService<GatewayHealthService>().Should().NotBeNull();
        provider.GetRequiredService<GatewayReadinessService>().Should().NotBeNull();
        provider.GetRequiredService<GatewayStatsService>().Should().NotBeNull();
        provider.GetRequiredService<GatewayProcessClock>().Should().NotBeNull();
        provider.GetRequiredService<IErrorResponseWriter>().Should().BeOfType<OpenAiErrorResponseWriter>();
    }
}
