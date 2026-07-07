using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pol33.Security.Configuration;
using Pol33.Security.DependencyInjection;

namespace Pol33.Security.Tests.DependencyInjection;

public sealed class GatewaySecurityOptionsValidatorTests
{
    [Fact]
    public void Validate_ProductionWithDefaultPepper_Fails()
    {
        var result = Validate("Production", new GatewaySecurityOptions
        {
            KeyPepper = GatewaySecurityOptions.DefaultKeyPepper,
        });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ProductionWithShortPepper_Fails()
    {
        var result = Validate("Production", new GatewaySecurityOptions { KeyPepper = "too-short" });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ProductionWithStrongPepper_Succeeds()
    {
        var result = Validate("Production", new GatewaySecurityOptions
        {
            KeyPepper = "a-sufficiently-long-production-pepper",
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_DevelopmentWithDefaultPepper_Succeeds()
    {
        var result = Validate("Development", new GatewaySecurityOptions
        {
            KeyPepper = GatewaySecurityOptions.DefaultKeyPepper,
        });

        result.Succeeded.Should().BeTrue();
    }

    private static ValidateOptionsResult Validate(string environment, GatewaySecurityOptions options) =>
        new GatewaySecurityOptionsValidator(new FakeEnvironment(environment)).Validate(null, options);

    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
