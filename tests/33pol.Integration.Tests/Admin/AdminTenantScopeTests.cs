using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// What the admin surface answers on a gateway with no API key store, and what it answers with a
/// credential.
/// </summary>
/// <remarks>
/// These began as tests of a narrower question: which refusal a tenant-scoped handler owes a caller
/// that reached it with no tenant. A database-less gateway authenticated nobody, so every admin
/// request arrived tenant-less, and answering <c>401 Unauthorized</c> made the console treat a
/// working credential as revoked — polling stopped, the live stream dropped, the operator was told
/// to sign in again.
/// <para>
/// GW-01 removed the premise rather than the symptom. The control plane is no longer reachable
/// without a credential in any configuration, that gateway included, so those handlers are never
/// entered anonymously: 401 is now both correct and the only answer an anonymous caller gets. That
/// is what is pinned below — the whole admin surface refuses anonymity, and answers a real admin
/// key. <c>AdminTenantScope.Denied()</c> stays as defence for a credential carrying no tenant, a
/// state no current authentication path can produce.
/// </para>
/// </remarks>
public sealed class AdminTenantScopeTests
{
    /// <summary>
    /// A gateway with no database: no key store, no tenant, and — since GW-01 — no control plane
    /// either. It is the configuration the console is run in for local development, and the one
    /// that used to serve the admin API to anyone who could reach the port.
    /// </summary>
    private static WebApplicationFactory<Program> CreateKeylessGateway() =>
        GatewayWebApplicationFactory.Create();

    public static TheoryData<string, string> TenantScopedReads() => new()
    {
        { "GET", "/admin/api/keys" },
        { "GET", "/admin/api/keys?includeUsageSummary=true&includeArchived=true" },
        { "GET", "/admin/api/tenant/model-grants" },
        { "GET", "/admin/api/usage" },
        { "GET", "/admin/api/usage/events" },
        { "GET", "/admin/api/usage/forecast?days=7" },
    };

    [Theory]
    [MemberData(nameof(TenantScopedReads))]
    public async Task TenantScopedEndpoints_WithoutACredential_Are401(string method, string path)
    {
        using var factory = CreateKeylessGateway();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "no credential reached the gateway, and a gateway without a key store does not make the "
            + "control plane anonymous");
    }

    /// <summary>The mutating half of the same surface answers identically.</summary>
    public static TheoryData<string, string, string> TenantScopedWrites() => new()
    {
        { "POST", "/admin/api/keys", """{"role":"Inference"}""" },
        { "POST", "/admin/api/keys/revoke", """{"keyIds":["00000000-0000-0000-0000-000000000001"]}""" },
        { "PUT", "/admin/api/tenant/model-grants", """{"modelIds":[]}""" },
    };

    [Theory]
    [MemberData(nameof(TenantScopedWrites))]
    public async Task TenantScopedMutations_WithoutACredential_Are401(string method, string path, string body)
    {
        using var factory = CreateKeylessGateway();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The GW-01 regression proper. These four need no tenant, and on a database-less gateway they
    /// answered <c>200</c> to anyone — the model inventory, the captured error stream, the request
    /// log and the traffic summary, served without a credential.
    /// </summary>
    [Theory]
    [InlineData("/admin/api/summary")]
    [InlineData("/admin/api/config/status")]
    [InlineData("/admin/api/logs?limit=1")]
    [InlineData("/admin/api/errors/groups?limit=1")]
    public async Task NonTenantScopedEndpoints_WithoutACredential_AreAlso401(string path)
    {
        using var factory = CreateKeylessGateway();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The other half of the contract: refusing anonymity must not have closed the surface to an
    /// operator holding a key. Every route above answers on a gateway that has one.
    /// </summary>
    [Theory]
    [InlineData("/admin/api/summary")]
    [InlineData("/admin/api/config/status")]
    [InlineData("/admin/api/logs?limit=1")]
    [InlineData("/admin/api/errors/groups?limit=1")]
    [InlineData("/admin/api/keys")]
    [InlineData("/admin/api/tenant/model-grants")]
    [InlineData("/admin/api/usage")]
    public async Task WithAnAdminKey_TheAdminSurfaceAnswers(string path)
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = factory.CreateAdminClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the credential is scoped to a tenant, so nothing here is out of reach");
    }

    /// <summary>
    /// A request with no credential at all, on a gateway that has keys, is a 401 — the case that was
    /// already correct and must stay that way.
    /// </summary>
    [Fact]
    public async Task AMissingCredential_IsStillA401()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/api/keys");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
