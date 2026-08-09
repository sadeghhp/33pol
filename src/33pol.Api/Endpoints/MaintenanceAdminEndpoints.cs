using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class MaintenanceAdminEndpoints
{
    public static IEndpointRouteBuilder MapMaintenanceAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/api/maintenance/backup", CreateBackupAsync)
            .RequireAuthorization(GatewayAuthPolicies.Operator);
        return endpoints;
    }

    private static async Task<IResult> CreateBackupAsync(
        HttpContext httpContext,
        ISqliteBackupService backupService,
        IAuditLogger audit,
        CancellationToken cancellationToken)
    {
        var result = await backupService.CreateBackupAsync(cancellationToken).ConfigureAwait(false);

        audit.LogAdminAction(
            "maintenance.backup",
            new AuditLogEntry(
                httpContext.User.FindFirst(GatewayAuthClaims.TenantId)?.Value,
                httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                new { result.Succeeded, result.Path, result.SizeBytes, result.IntegrityCheck }));

        // 200 on a verified backup, 503 when no database is configured, 500 on a produced-but-corrupt copy.
        var statusCode = result switch
        {
            { Succeeded: true } => StatusCodes.Status200OK,
            { Path: null } => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Results.Json(result, statusCode: statusCode);
    }
}
