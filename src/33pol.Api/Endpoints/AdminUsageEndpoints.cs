using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Core.Abstractions;
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

        return endpoints;
    }

    private static async Task<IResult> GetUsage(
        IBillingUsageService usageService,
        DateOnly? from,
        DateOnly? to,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var report = await usageService
            .GetUsageReportAsync(
                new UsageReportRequest { FromDate = from, ToDate = to, TenantId = tenantId },
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(report);
    }

    private static async Task<IResult> ExportUsage(
        IBillingUsageService usageService,
        DateOnly? from,
        DateOnly? to,
        Guid? tenantId,
        string? format,
        CancellationToken cancellationToken)
    {
        var report = await usageService
            .GetUsageReportAsync(
                new UsageReportRequest { FromDate = from, ToDate = to, TenantId = tenantId },
                cancellationToken)
            .ConfigureAwait(false);

        var export = usageService.ExportRollups(report.Rollups, format ?? "json");
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(export.Body),
            export.ContentType,
            export.FileName);
    }
}
