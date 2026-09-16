using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Api.Endpoints;
using Pol33.Core.Identity;

namespace Pol33.Api.Tests.Endpoints;

/// <summary>
/// The refusal a tenant-scoped admin handler owes a caller that reached it without a tenant.
/// </summary>
/// <remarks>
/// Every one of these handlers sits behind <c>RequireAuthorization(Admin)</c>, so reaching them
/// means authentication has already succeeded. They answered <c>401 Unauthorized</c> anyway, and
/// the admin console reads any 401 as "this credential has been rejected" — one request to Keys or
/// Usage stopped the 2s poll, dropped the live stream, froze the Overview on figures it kept
/// presenting as current, and told the operator to sign in again with a key that was fine.
/// <para>
/// Pinned at this level rather than over HTTP because the path is no longer reachable end to end:
/// the control plane now refuses anonymous callers in every configuration, and every authentication
/// path that succeeds attaches a tenant. This is the contract the guard keeps for a state nothing
/// can currently produce — which is exactly the kind that rots unobserved.
/// </para>
/// </remarks>
public sealed class AdminTenantScopeTests
{
    private static DefaultHttpContext WithTenant(string? tenantId)
    {
        var context = new DefaultHttpContext();
        if (tenantId is not null)
        {
            context.Items[TenantContextKeys.HttpContextItemKey] = new TenantContext { TenantId = tenantId, ApiKeyId = Guid.NewGuid().ToString() };
        }

        return context;
    }

    [Fact]
    public void TryResolve_WithATenant_ReturnsIt()
    {
        var tenant = Guid.NewGuid();

        AdminTenantScope.TryResolve(WithTenant(tenant.ToString()), out var resolved).Should().BeTrue();

        resolved.Should().Be(tenant);
    }

    [Fact]
    public void TryResolve_WithNoTenantContext_Fails()
    {
        AdminTenantScope.TryResolve(WithTenant(null), out var resolved).Should().BeFalse();
        resolved.Should().Be(Guid.Empty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void TryResolve_WithAnUnusableTenantId_Fails(string tenantId)
    {
        AdminTenantScope.TryResolve(WithTenant(tenantId), out var resolved).Should().BeFalse();
        resolved.Should().Be(Guid.Empty);
    }

    /// <summary>
    /// 403, not 401. The credential is valid and was accepted; it simply cannot reach this resource,
    /// and saying "unauthenticated" makes every client throw the session away.
    /// </summary>
    [Fact]
    public async Task Denied_Is403WithACodeAndAnExplanation()
    {
        var context = new DefaultHttpContext();
        // JsonHttpResult resolves its logger and JSON options from the request services.
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await AdminTenantScope.Denied().ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        body.Position = 0;
        var json = await new StreamReader(body).ReadToEndAsync();
        json.Should().Contain("tenant_context_required", "clients branch on the code, not the prose");
        json.Should().Contain("ConnectionStrings:GatewayDb", "the operator needs to know what to change");
    }
}
