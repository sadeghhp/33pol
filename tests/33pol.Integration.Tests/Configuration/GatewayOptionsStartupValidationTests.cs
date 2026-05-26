using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Pol33.Integration.Tests.Configuration;

public sealed class GatewayOptionsStartupValidationTests
{
    [Fact]
    public void Host_WithInvalidGatewayOptions_FailsOnBuild()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:ConfigReloadIntervalSeconds"] = "-1",
                });
            });
        });

        var act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>();
    }
}
