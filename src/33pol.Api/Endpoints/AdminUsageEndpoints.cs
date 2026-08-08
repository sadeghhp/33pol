using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class AdminUsageEndpoints
{
    public static IEndpointRouteBuilder MapAdminUsageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api/usage")
            .RequireAuthorization(GatewayAuthPolicies.Admin);

        group.MapGet("/", GetUsage);
        group.MapGet("/export", ExportUsage);
        group.MapGet("/forecast", GetForecast);
        group.MapGet("/events", GetEvents);

        return endpoints;
    }

    private static async Task<IResult> GetUsage(
        HttpContext httpContext,
        IBillingUsageService usageService,
        DateOnly? from,
        DateOnly? to,
        string? costCenter,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var report = await usageService
            .GetUsageReportAsync(
                new UsageReportRequest
                {
                    FromDate = from ?? DefaultFromDate(to),
                    ToDate = to,
                    TenantId = tenantId,
                    CostCenter = costCenter,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(report);
    }

    private static async Task<IResult> GetForecast(
        HttpContext httpContext,
        IBillingForecastService forecastService,
        int? days,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var report = await forecastService
            .GetForecastAsync(tenantId, days ?? 7, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(report);
    }

    private static async Task<IResult> GetEvents(
        HttpContext httpContext,
        IBillingUsageService usageService,
        DateOnly? from,
        DateOnly? to,
        Guid? apiKeyId,
        string? costCenter,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var page = await usageService
            .QueryEventsAsync(
                new BillingEventQuery(from, to, tenantId, apiKeyId, costCenter, limit ?? 100),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(page);
    }

    private static async Task<IResult> ExportUsage(
        HttpContext httpContext,
        IBillingUsageService usageService,
        DateOnly? from,
        DateOnly? to,
        string? costCenter,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return Results.Unauthorized();
        }

        var report = await usageService
            .GetUsageReportAsync(
                new UsageReportRequest
                {
                    FromDate = from ?? DefaultFromDate(to),
                    ToDate = to,
                    TenantId = tenantId,
                    CostCenter = costCenter,
                },
                cancellationToken)
            .ConfigureAwait(false);

        var export = usageService.ExportRollups(report.Rollups, format ?? "json");
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(export.Body),
            export.ContentType,
            export.FileName);
    }

    /// <summary>Window applied when the caller supplies no <c>from</c> date.</summary>
    /// <remarks>
    /// The rollup query has no row limit and both date bounds were optional, so a parameterless call
    /// materialised every rollup row ever written and serialised the lot into one response. A
    /// bounded default keeps the common call cheap; callers wanting more supply an explicit range.
    /// </remarks>
    private const int DefaultUsageWindowDays = 30;

    private static DateOnly DefaultFromDate(DateOnly? toDate) =>
        (toDate ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(-DefaultUsageWindowDays);

    private static bool TryGetTenantId(HttpContext context, out Guid tenantId)
    {
        tenantId = default;
        if (!context.Items.TryGetValue(TenantContextKeys.HttpContextItemKey, out var value) ||
            value is not TenantContext tenant)
        {
            return false;
        }

        return Guid.TryParse(tenant.TenantId, out tenantId);
    }
}
