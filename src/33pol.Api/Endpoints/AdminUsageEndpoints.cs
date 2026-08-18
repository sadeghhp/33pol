using Microsoft.AspNetCore.Authorization;
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
    /// <summary>
    /// Sentinel accepted in <c>costCenter</c> to select rows that have <em>no</em> cost centre.
    /// </summary>
    public const string NoCostCenterSentinel = "(none)";

    /// <summary>Longest inclusive window one report may span, in days.</summary>
    public const int MaxRangeDays = 366;

    /// <summary>
    /// Window applied when the caller supplies no <c>from</c> date: 30 days inclusive of <c>to</c>.
    /// </summary>
    /// <remarks>
    /// The rollup query has no row limit and both date bounds were optional, so a parameterless call
    /// materialised every rollup row ever written and serialised the lot into one response. A
    /// bounded default keeps the common call cheap; callers wanting more supply an explicit range.
    /// </remarks>
    public const int DefaultUsageWindowDays = 30;

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

    /// <summary>The one filter shape every usage endpoint accepts.</summary>
    /// <remarks>
    /// Bound by hand rather than <c>[AsParameters]</c> so the same validation (range order, range
    /// length, the <c>(none)</c> sentinel) is applied identically on all four routes.
    /// </remarks>
    private sealed record UsageFilter(
        Guid TenantId,
        DateOnly From,
        DateOnly To,
        string? CostCenter,
        bool NoCostCenter,
        Guid? ApiKeyId,
        bool IncludeAnonymous)
    {
        public UsageScope Scope => new(TenantId, IncludeAnonymous);

        public UsageReportRequest ToReportRequest() => new()
        {
            FromDate = From,
            ToDate = To,
            TenantId = TenantId,
            IncludeAnonymous = IncludeAnonymous,
            CostCenter = CostCenter,
            NoCostCenter = NoCostCenter,
            ApiKeyId = ApiKeyId,
        };

        public BillingEventQuery ToEventQuery(int limit, BillingEventCursor? cursor) => new(
            From,
            To,
            TenantId,
            ApiKeyId,
            CostCenter,
            limit,
            IncludeAnonymous,
            NoCostCenter,
            cursor);
    }

    /// <summary>
    /// <c>includeAnonymous</c> is honoured only for callers who satisfy the Operator policy.
    /// </summary>
    /// <remarks>
    /// The group is gated by the per-tenant Admin policy. Anonymous rows (traffic to public models
    /// sent without any key) belong to no tenant, so letting any tenant's admin opt into them
    /// exposed the whole gateway's anonymous request volume, model ids and cost to every tenant.
    /// A non-operator caller asking for them gets a tenant-scoped report, not an error: the flag is
    /// forced to <c>false</c> rather than rejected so existing clients keep working.
    /// </remarks>
    private static async Task<(UsageFilter? Filter, IResult? Error)> TryBindFilterAsync(
        HttpContext httpContext,
        IAuthorizationService authorization,
        DateOnly? from,
        DateOnly? to,
        string? costCenter,
        Guid? apiKeyId,
        bool? includeAnonymous)
    {
        if (!TryGetTenantId(httpContext, out var tenantId))
        {
            return (null, Results.Unauthorized());
        }

        var anonymousAllowed = false;
        if (includeAnonymous == true)
        {
            var operatorCheck = await authorization
                .AuthorizeAsync(httpContext.User, httpContext, GatewayAuthPolicies.Operator)
                .ConfigureAwait(false);
            anonymousAllowed = operatorCheck.Succeeded;
        }

        var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var fromDate = from ?? toDate.AddDays(-(DefaultUsageWindowDays - 1));

        if (fromDate > toDate)
        {
            return (null, BadRequest("invalid_range", "'from' must be on or before 'to'."));
        }

        if (toDate.DayNumber - fromDate.DayNumber + 1 > MaxRangeDays)
        {
            return (null, BadRequest("range_too_long", $"The date range may span at most {MaxRangeDays} days."));
        }

        var trimmed = string.IsNullOrWhiteSpace(costCenter) ? null : costCenter.Trim();
        var noCostCenter = string.Equals(trimmed, NoCostCenterSentinel, StringComparison.OrdinalIgnoreCase);

        return (new UsageFilter(
            tenantId,
            fromDate,
            toDate,
            noCostCenter ? null : trimmed,
            noCostCenter,
            apiKeyId,
            anonymousAllowed), null);
    }

    private static async Task<IResult> GetUsage(
        HttpContext httpContext,
        IAuthorizationService authorization,
        IBillingUsageService usageService,
        DateOnly? from,
        DateOnly? to,
        string? costCenter,
        Guid? apiKeyId,
        bool? includeAnonymous,
        CancellationToken cancellationToken)
    {
        var (filter, error) = await TryBindFilterAsync(httpContext, authorization, from, to, costCenter, apiKeyId, includeAnonymous)
            .ConfigureAwait(false);
        if (filter is null)
        {
            return error!;
        }

        var report = await usageService
            .GetUsageReportAsync(filter.ToReportRequest(), cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(report);
    }

    private static async Task<IResult> GetForecast(
        HttpContext httpContext,
        IAuthorizationService authorization,
        IBillingForecastService forecastService,
        int? days,
        string? costCenter,
        Guid? apiKeyId,
        bool? includeAnonymous,
        CancellationToken cancellationToken)
    {
        // The forecast has its own window (trailing complete days + month to date), so the report's
        // from/to are deliberately not accepted here; the other filters are shared.
        var (filter, error) = await TryBindFilterAsync(httpContext, authorization, null, null, costCenter, apiKeyId, includeAnonymous)
            .ConfigureAwait(false);
        if (filter is null)
        {
            return error!;
        }

        var report = await forecastService
            .GetForecastAsync(
                new UsageForecastRequest
                {
                    Scope = filter.Scope,
                    CostCenter = filter.CostCenter,
                    NoCostCenter = filter.NoCostCenter,
                    ApiKeyId = filter.ApiKeyId,
                    TrailingDays = days ?? 7,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(report);
    }

    private static async Task<IResult> GetEvents(
        HttpContext httpContext,
        IAuthorizationService authorization,
        IBillingUsageService usageService,
        DateOnly? from,
        DateOnly? to,
        Guid? apiKeyId,
        string? costCenter,
        bool? includeAnonymous,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var (filter, error) = await TryBindFilterAsync(httpContext, authorization, from, to, costCenter, apiKeyId, includeAnonymous)
            .ConfigureAwait(false);
        if (filter is null)
        {
            return error!;
        }

        BillingEventCursor? decoded = null;
        if (!string.IsNullOrWhiteSpace(cursor) && !BillingEventCursor.TryDecode(cursor, out decoded))
        {
            return BadRequest("invalid_cursor", "The 'cursor' value is not a cursor issued by this endpoint.");
        }

        var page = await usageService
            .QueryEventsAsync(filter.ToEventQuery(limit ?? 100, decoded), cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(page);
    }

    private static async Task<IResult> ExportUsage(
        HttpContext httpContext,
        IAuthorizationService authorization,
        IBillingUsageService usageService,
        DateOnly? from,
        DateOnly? to,
        string? costCenter,
        Guid? apiKeyId,
        bool? includeAnonymous,
        string? format,
        string? dataset,
        CancellationToken cancellationToken)
    {
        var (filter, error) = await TryBindFilterAsync(httpContext, authorization, from, to, costCenter, apiKeyId, includeAnonymous)
            .ConfigureAwait(false);
        if (filter is null)
        {
            return error!;
        }

        var fmt = (format ?? "json").Trim().ToLowerInvariant();
        if (fmt is not ("json" or "csv"))
        {
            return BadRequest("invalid_format", "'format' must be 'json' or 'csv'.");
        }

        var set = (dataset ?? "rollups").Trim().ToLowerInvariant();
        UsageExportResult export;
        switch (set)
        {
            case "rollups":
                var report = await usageService
                    .GetUsageReportAsync(filter.ToReportRequest(), cancellationToken)
                    .ConfigureAwait(false);
                export = usageService.ExportRollups(report.Rollups, fmt);
                break;
            case "events":
                export = await usageService
                    .ExportEventsAsync(filter.ToEventQuery(UsageExportLimits.MaxEventRows, null), fmt, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                return BadRequest("invalid_dataset", "'dataset' must be 'rollups' or 'events'.");
        }

        httpContext.Response.Headers["X-Export-Truncated"] = export.Truncated ? "true" : "false";
        httpContext.Response.Headers["Access-Control-Expose-Headers"] = "Content-Disposition, X-Export-Truncated";
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(export.Body),
            export.ContentType,
            export.FileName);
    }

    private static IResult BadRequest(string code, string message) =>
        Results.Json(new { code, message }, statusCode: StatusCodes.Status400BadRequest);

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
