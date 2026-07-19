using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Pol33.Persistence.Bootstrap;

namespace Pol33.Persistence.Tests.Bootstrap;

public sealed class GatewayBootstrapOptionsValidatorTests
{
    private const string StrongPepper = "a-sufficiently-long-production-pepper";
    private const string StrongAdminKey = "sk-33pol-8f2c9a1b7d6e4f0a3c5b9d2e";

    [Fact]
    public void Production_DefaultPepper_Fails()
    {
        var result = Validate("Production", pepper: "dev-pepper-change-me", securityPepper: "dev-pepper-change-me");

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Production_ShippedComposeDefaultPepper_Fails()
    {
        var result = Validate("Production", pepper: "oJHJdzSvNdVFbFd8fDrexL3bf6n9ggW", securityPepper: "oJHJdzSvNdVFbFd8fDrexL3bf6n9ggW");

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Production_MismatchedPeppers_Fails()
    {
        var result = Validate("Production", pepper: StrongPepper, securityPepper: StrongPepper + "-different");

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("identical");
    }

    [Fact]
    public void Production_ShippedDefaultAdminKey_Fails()
    {
        var result = Validate(
            "Production",
            pepper: StrongPepper,
            securityPepper: StrongPepper,
            adminKey: "sk-33pol-4aa283ddb877adaccc60cb53314fa15cfd41f01084df064c");

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Production_StrongMatchingSecrets_Succeeds()
    {
        var result = Validate("Production", pepper: StrongPepper, securityPepper: StrongPepper, adminKey: StrongAdminKey);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Production_NoAdminKey_Succeeds()
    {
        // An already-seeded database does not require the admin key to be supplied.
        var result = Validate("Production", pepper: StrongPepper, securityPepper: StrongPepper, adminKey: null);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Development_DefaultsAndWeakSecrets_Succeeds()
    {
        var result = Validate("Development", pepper: "dev-pepper-change-me", securityPepper: "different", adminKey: "sk-33pol-dev-local-unsafe");

        result.Succeeded.Should().BeTrue();
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(
        string environment,
        string pepper,
        string securityPepper,
        string? adminKey = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Security:KeyPepper"] = securityPepper,
            })
            .Build();

        var options = new GatewayBootstrapOptions { KeyPepper = pepper, AdminApiKey = adminKey };
        return new GatewayBootstrapOptionsValidator(new FakeEnvironment(environment), config).Validate(null, options);
    }

    private sealed class FakeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
