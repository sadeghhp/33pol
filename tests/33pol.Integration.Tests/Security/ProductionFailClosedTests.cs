using System.Net;
using Microsoft.Extensions.Hosting;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Security;

/// <summary>
/// GW-01, end to end: what a Production host does with the connection string the project actually
/// ships, and what the control plane answers once it is configured properly.
/// </summary>
/// <remarks>
/// <c>appsettings.json</c> ships <c>"GatewayDb": ""</c> and the aspnet base image leaves
/// <c>ASPNETCORE_ENVIRONMENT</c> unset, which means Production. That combination used to start
/// cleanly and serve the whole admin API to anonymous callers: the fail-closed guard lived inside a
/// hosted service registered only on the branch taken when a connection string <em>was</em> present,
/// so it could never run in the one configuration it was written for.
/// </remarks>
public sealed class ProductionFailClosedTests
{
    /// <summary>Every control-plane surface the GW-01 reproduction reached anonymously.</summary>
    public static TheoryData<string> ControlPlaneProbes() => new()
    {
        "/admin/api/rate-limits",
        "/admin/api/config/status",
        "/admin/api/cors",
        "/admin/api/errors/groups?limit=1",
        "/admin/api/summary",
        "/stats",
    };

    /// <summary>
    /// The regression. A Production host with no database has no key store, so it cannot
    /// authenticate anyone; starting anyway is how the control plane ended up open. It now refuses
    /// to be configured at all, which happens before Kestrel binds a port.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void OutsideDevelopment_WithoutAConnectionString_TheHostRefusesToStart(string environmentName)
    {
        using var factory = GatewayWebApplicationFactory.Create(environmentName: environmentName);

        var act = () => factory.CreateClient();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:GatewayDb*")
            .And.Message.Should().Contain("AllowAnonymous");
    }

    /// <summary>Development is allowed to run open, and still does.</summary>
    [Fact]
    public async Task InDevelopment_WithoutAConnectionString_TheHostStarts()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// The escape hatch, pinned deliberately: an operator who means to run without authentication
    /// says so, and then the host starts. What they get is an inference gateway, not an
    /// administrative one — anonymous inference is served, the control plane is not.
    /// </summary>
    [Theory]
    [MemberData(nameof(ControlPlaneProbes))]
    public async Task WithTheAnonymousOptIn_TheControlPlaneIsStillClosed(string path)
    {
        using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings => settings["Gateway:Security:AllowAnonymous"] = "true");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "opting into anonymous inference is not opting into an anonymous control plane");
    }

    /// <summary>A reload is the write half of the same surface, and is refused the same way.</summary>
    [Fact]
    public async Task WithTheAnonymousOptIn_TheControlPlaneCannotBeWrittenTo()
    {
        using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings => settings["Gateway:Security:AllowAnonymous"] = "true");
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/admin/api/config/reload", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// And the mode keeps doing what it exists for: a gateway with no key store still lists and
    /// serves models without a credential.
    /// </summary>
    [Fact]
    public async Task WithTheAnonymousOptIn_InferenceIsStillAnonymous()
    {
        using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings => settings["Gateway:Security:AllowAnonymous"] = "true");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Probes never need a credential, in this mode or any other.</summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public async Task WithTheAnonymousOptIn_ProbesStayAnonymous(string path)
    {
        using var factory = GatewayWebApplicationFactory.Create(
            environmentName: Environments.Production,
            configureSettings: settings => settings["Gateway:Security:AllowAnonymous"] = "true");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A correctly configured Production gateway: the same probes are refused without a credential
    /// and answered with an Operator one. This is the acceptance criterion GW-01 was written
    /// against, and the reproduction that produced six 200s before the fix.
    /// </summary>
    [Theory]
    [MemberData(nameof(ControlPlaneProbes))]
    public async Task ConfiguredProduction_RefusesAnonymously_AndAnswersAnOperatorKey(string path)
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            environmentName: Environments.Production);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var operatorClient = factory.CreateAdminClient();
        (await operatorClient.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>The write half, against the same configured host.</summary>
    [Fact]
    public async Task ConfiguredProduction_ReloadRefusesAnonymously_AndAnswersAnOperatorKey()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            environmentName: Environments.Production);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var anonymous = factory.CreateClient();
        (await anonymous.PostAsync("/admin/api/config/reload", content: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        using var operatorClient = factory.CreateAdminClient();
        (await operatorClient.PostAsync("/admin/api/config/reload", content: null)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }
}
