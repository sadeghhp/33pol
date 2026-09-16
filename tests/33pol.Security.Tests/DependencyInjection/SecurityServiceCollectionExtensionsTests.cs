using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Persistence.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Security.Configuration;
using Pol33.Security.DependencyInjection;
using Pol33.Security.Hosting;

namespace Pol33.Security.Tests.DependencyInjection;

public sealed class SecurityServiceCollectionExtensionsTests
{
    private static IHostEnvironment Env(string name)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    private static (string Key, string Value) ConnectionString() =>
        ($"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}",
         "Host=localhost;Database=test");

    /// <summary>
    /// Without a database there is no key store, so the gateway serves every endpoint — the admin
    /// control plane included — anonymously. Development is allowed to do that.
    /// </summary>
    [Fact]
    public void AddGatewaySecurity_WithoutConnectionString_InDevelopment_RegistersNullValidator()
    {
        var services = new ServiceCollection();

        services.AddGatewaySecurity(new ConfigurationBuilder().Build(), Env(Environments.Development));

        services.Any(d => d.ServiceType == typeof(IApiKeyValidator)).Should().BeTrue();
    }

    /// <summary>
    /// The initializer is what sets <c>IsAuthenticationRequired</c> and logs the warning that the
    /// gateway is running open. It used to be registered only when a connection string was present,
    /// which is how the anonymous fallback became silent — no flag set, no warning, and the
    /// fail-closed guard living inside it unreachable in the one configuration it was written for.
    /// </summary>
    [Fact]
    public void AddGatewaySecurity_WithoutConnectionString_StillRegistersTheAuthenticationInitializer()
    {
        var services = new ServiceCollection();

        services.AddGatewaySecurity(new ConfigurationBuilder().Build(), Env(Environments.Development));

        services.Any(d => d.ServiceType == typeof(IHostedService)
                          && d.ImplementationType == typeof(GatewayAuthenticationInitializer))
            .Should().BeTrue();
    }

    /// <summary>
    /// The regression this file exists for: a Production deploy shipping the default (empty)
    /// connection string must not start. It used to start, serve <c>/admin/api/summary</c>,
    /// <c>/admin/api/config/status</c>, <c>DELETE /admin/api/logs</c> and <c>/v1/models</c> to
    /// anonymous callers, and log nothing about it.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void AddGatewaySecurity_WithoutConnectionString_OutsideDevelopment_Throws(string environmentName)
    {
        var services = new ServiceCollection();

        var act = () => services.AddGatewaySecurity(new ConfigurationBuilder().Build(), Env(environmentName));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:GatewayDb*")
            .And.Message.Should().Contain("AllowAnonymous");
    }

    /// <summary>
    /// An unknown environment is Production as far as <see cref="IHostEnvironment"/> is concerned,
    /// and must be here too: "nobody set ASPNETCORE_ENVIRONMENT" is not consent to run open.
    /// </summary>
    [Fact]
    public void AddGatewaySecurity_WithoutConnectionString_AndNoEnvironment_Throws()
    {
        var services = new ServiceCollection();

        var act = () => services.AddGatewaySecurity(new ConfigurationBuilder().Build());

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>The environment falls back to configuration when no host environment is passed.</summary>
    [Fact]
    public void AddGatewaySecurity_WithoutConnectionString_ReadsTheEnvironmentFromConfiguration()
    {
        var services = new ServiceCollection();

        var act = () => services.AddGatewaySecurity(Config(("ASPNETCORE_ENVIRONMENT", "Development")));

        act.Should().NotThrow();
    }

    /// <summary>Running open stays possible — but only by saying so.</summary>
    [Fact]
    public void AddGatewaySecurity_WithoutConnectionString_OutsideDevelopment_AllowsExplicitOptIn()
    {
        var services = new ServiceCollection();

        var act = () => services.AddGatewaySecurity(
            Config(("Gateway:Security:AllowAnonymous", "true")), Env(Environments.Production));

        act.Should().NotThrow();
        services.Any(d => d.ServiceType == typeof(IApiKeyValidator)).Should().BeTrue();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    public void AddGatewaySecurity_WithoutConnectionString_OutsideDevelopment_RejectsAnythingButATrueOptIn(string value)
    {
        var services = new ServiceCollection();

        var act = () => services.AddGatewaySecurity(
            Config(("Gateway:Security:AllowAnonymous", value)), Env(Environments.Production));

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// The key pepper encrypts the upstream provider secrets file, which a gateway without a
    /// database still reads and writes, so its validator has to be registered on that branch too.
    /// </summary>
    [Fact]
    public void AddGatewaySecurity_WithoutConnectionString_StillValidatesSecurityOptions()
    {
        var services = new ServiceCollection();

        services.AddGatewaySecurity(new ConfigurationBuilder().Build(), Env(Environments.Development));

        services.Any(d => d.ServiceType == typeof(IValidateOptions<GatewaySecurityOptions>)
                          && d.ImplementationType == typeof(GatewaySecurityOptionsValidator))
            .Should().BeTrue();
    }

    /// <summary>A configured database is the normal path and must be unaffected by the guard.</summary>
    [Fact]
    public void AddGatewaySecurity_WithConnectionString_OutsideDevelopment_DoesNotThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddGatewaySecurity(Config(ConnectionString()), Env(Environments.Production));

        act.Should().NotThrow();
    }

    [Fact]
    public async Task AddGatewaySecurity_WithoutConnectionString_NullImplementationsAreUsable()
    {
        var services = new ServiceCollection();
        services.AddGatewaySecurity(new ConfigurationBuilder().Build(), Env(Environments.Development));
        var provider = services.BuildServiceProvider();

        var validator = provider.GetRequiredService<IApiKeyValidator>();
        (await validator.ValidateAsync(null)).IsSuccess.Should().BeFalse();
        validator.InvalidateCache(Guid.NewGuid());

        var grantService = provider.GetRequiredService<IModelGrantService>();
        (await grantService.IsModelAllowedAsync(Guid.NewGuid(), Guid.NewGuid(), "model")).Should().BeTrue();
        grantService.InvalidateTenantGrants(Guid.NewGuid());
        grantService.InvalidateApiKeyGrants(Guid.NewGuid());

        var grantAdmin = provider.GetRequiredService<IModelGrantAdminService>();
        var grantAdminAct = () => grantAdmin.GetTenantGrantsAsync(Guid.NewGuid());
        await grantAdminAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:GatewayDb*");

        var adminKeys = provider.GetRequiredService<IAdminKeyService>();
        var adminKeysAct = () => adminKeys.ListAsync(Guid.NewGuid());
        await adminKeysAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:GatewayDb*");
    }

    [Fact]
    public void AddGatewaySecurity_WithConnectionString_RegistersAuthenticationServices()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{PersistenceServiceCollectionExtensions.ConnectionStringName}"] =
                    "Host=localhost;Database=test",
            })
            .Build();

        services.AddGatewaySecurity(configuration);

        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GatewayAuthenticationInitializer))
            .Should().BeTrue();
    }
}
