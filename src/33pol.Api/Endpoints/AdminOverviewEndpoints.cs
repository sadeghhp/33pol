using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Core.Abstractions;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

/// <summary>
/// The Overview's slow sections. Each is served from a short in-process memo; <c>?refresh=true</c>
/// forces a rebuild. A section whose data source is not configured (no database, no audit log)
/// answers 204 so the console hides the card instead of showing an error.
/// </summary>
public static class AdminOverviewEndpoints
{
    public static IEndpointRouteBuilder MapAdminOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api/overview")
            .RequireAuthorization(GatewayAuthPolicies.Operator);

        group.MapGet("/finops", GetFinOps);
        group.MapGet("/policy", GetPolicy);
        group.MapGet("/control-plane", GetControlPlane);
        group.MapGet("/activity", GetActivity);
        group.MapGet("/tenants", GetTenants);

        return endpoints;
    }

    private static async Task<IResult> GetFinOps(IOverviewSectionService sections, bool? refresh, CancellationToken cancellationToken) =>
        Section(await sections.GetFinOpsAsync(refresh == true, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetPolicy(IOverviewSectionService sections, bool? refresh, CancellationToken cancellationToken) =>
        Section(await sections.GetPolicyAsync(refresh == true, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetControlPlane(IOverviewSectionService sections, bool? refresh, CancellationToken cancellationToken) =>
        Section(await sections.GetControlPlaneAsync(refresh == true, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetActivity(IOverviewSectionService sections, int? limit, bool? refresh, CancellationToken cancellationToken) =>
        Section(await sections.GetActivityAsync(Math.Clamp(limit ?? 20, 1, 200), refresh == true, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetTenants(IOverviewSectionService sections, bool? refresh, CancellationToken cancellationToken) =>
        Section(await sections.GetTenantsAsync(refresh == true, cancellationToken).ConfigureAwait(false));

    private static IResult Section<T>(T? value) where T : class =>
        value is null ? Results.NoContent() : Results.Json(value);
}
