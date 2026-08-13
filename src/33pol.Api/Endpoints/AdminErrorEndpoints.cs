using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

/// <summary>
/// The admin Errors tab's API: grouped failures, their occurrences, filter facets, export, and the
/// operator-initiated clear.
/// </summary>
public static class AdminErrorEndpoints
{
    public static IEndpointRouteBuilder MapAdminErrorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api/errors")
            .RequireAuthorization(GatewayAuthPolicies.Operator);

        group.MapGet("/groups", ListGroups);
        group.MapGet("/facets", GetFacets);
        group.MapGet("/export", Export);
        // "/" and "" normalize to the same pattern, so registering both makes every request to the
        // bare route throw AmbiguousMatchException. One registration serves both spellings.
        group.MapGet("/", ListOccurrences);

        // Declared after the literal routes above so "/facets" is never captured as an id.
        group.MapGet("/{id}", GetOccurrence);

        group.MapDelete("/", ClearErrors);

        return endpoints;
    }

    private static async Task<IResult> ListGroups(
        IGatewayErrorStore store,
        [AsParameters] AdminErrorFilter filter,
        CancellationToken cancellationToken)
    {
        var page = await store.QueryGroupsAsync(filter.ToQuery(), cancellationToken).ConfigureAwait(false);
        return Results.Json(AdminErrorGroupListResponse.From(page, store.IsPersistent));
    }

    private static async Task<IResult> ListOccurrences(
        IGatewayErrorStore store,
        [AsParameters] AdminErrorFilter filter,
        CancellationToken cancellationToken)
    {
        var page = await store.QueryAsync(filter.ToQuery(), cancellationToken).ConfigureAwait(false);
        return Results.Json(AdminErrorListResponse.From(page, store.IsPersistent));
    }

    private static async Task<IResult> GetOccurrence(
        IGatewayErrorStore store,
        string id,
        CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return record is null
            ? Results.Json(
                new { error = new { code = "not_found", message = $"No error record with id '{id}'." } },
                statusCode: StatusCodes.Status404NotFound)
            : Results.Json(AdminErrorOccurrenceDto.From(record));
    }

    private static async Task<IResult> GetFacets(
        IGatewayErrorStore store,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var facets = await store.GetFacetsAsync(from, to, cancellationToken).ConfigureAwait(false);
        return Results.Json(AdminErrorFacetsDto.From(facets));
    }

    private static async Task<IResult> Export(
        IGatewayErrorStore store,
        [AsParameters] AdminErrorFilter filter,
        string? format,
        CancellationToken cancellationToken)
    {
        var query = filter.ToQuery() with
        {
            Limit = filter.Limit is > 0 ? filter.Limit.Value : 1000,
        };

        var page = await store
            .QueryAsync(query.Clamp(GatewayErrorQuery.MaxExportLimit), cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(AdminErrorListResponse.From(page, store.IsPersistent));
        }

        return Results.Text(ToCsv(page.Items), "text/csv", Encoding.UTF8);
    }

    /// <summary>
    /// Clears every recorded error, the error counters, and the persisted snapshot behind them.
    /// </summary>
    /// <remarks>
    /// <c>confirm=true</c> is required. This is destructive and unrecoverable, and a DELETE that a
    /// mistyped URL or an over-eager client can fire is not an acceptable shape for it.
    /// </remarks>
    private static async Task<IResult> ClearErrors(
        HttpContext httpContext,
        IGatewayErrorAdminService admin,
        IAuditLogger audit,
        bool? confirm,
        string? scope,
        CancellationToken cancellationToken)
    {
        if (confirm != true)
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = "confirmation_required",
                        message = "Pass confirm=true to clear all recorded errors and reset the error counters.",
                    },
                },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var clearScope = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
            ? GatewayErrorClearScope.AllCounters
            : GatewayErrorClearScope.Errors;

        var result = await admin.ClearAllAsync(clearScope, cancellationToken).ConfigureAwait(false);

        audit.LogAdminAction(
            "errors.clear",
            new AuditLogEntry(
                httpContext.User.FindFirst(GatewayAuthClaims.TenantId)?.Value,
                httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value,
                new
                {
                    scope = clearScope.ToString(),
                    result.RecordsDeleted,
                    result.RecentRequestRowsRemoved,
                    result.TotalErrorsCleared,
                    result.SnapshotRewritten,
                    result.DatabaseAvailable,
                }));

        return Results.Json(AdminErrorClearResponse.From(result));
    }

    private static string ToCsv(IReadOnlyList<GatewayErrorRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "timestampUtc,level,source,category,errorCode,outcome,statusCode,modelId,method,path," +
            "requestId,tenantId,durationMs,exceptionType,message,hint,upstreamTarget");

        foreach (var record in records)
        {
            builder
                .Append(Cell(record.OccurredAt.ToString("O", CultureInfo.InvariantCulture))).Append(',')
                .Append(Cell(record.Level)).Append(',')
                .Append(Cell(record.Source)).Append(',')
                .Append(Cell(record.Category)).Append(',')
                .Append(Cell(record.EventCode)).Append(',')
                .Append(Cell(record.Outcome)).Append(',')
                .Append(Cell(record.StatusCode.ToString(CultureInfo.InvariantCulture))).Append(',')
                .Append(Cell(record.ModelId)).Append(',')
                .Append(Cell(record.Method)).Append(',')
                .Append(Cell(record.Path)).Append(',')
                .Append(Cell(record.RequestId)).Append(',')
                .Append(Cell(record.TenantId)).Append(',')
                .Append(Cell(record.DurationMs?.ToString("F2", CultureInfo.InvariantCulture))).Append(',')
                .Append(Cell(record.ExceptionType)).Append(',')
                .Append(Cell(record.Message)).Append(',')
                .Append(Cell(record.Hint)).Append(',')
                .Append(Cell(record.UpstreamTarget))
                .AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Quotes a CSV cell, and neutralizes leading <c>= + - @</c> so a spreadsheet treats an error
    /// message as text rather than a formula. Error text is attacker-influenced — an upstream can
    /// put anything in a response body — so this is a real injection vector, not a formality.
    /// </summary>
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value;
        if (text[0] is '=' or '+' or '-' or '@')
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>Query-string filters shared by the group, occurrence and export endpoints.</summary>
public sealed class AdminErrorFilter
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? Level { get; init; }

    public string? ModelId { get; init; }

    public int? Status { get; init; }

    public string? Code { get; init; }

    public string? TenantId { get; init; }

    public string? RequestId { get; init; }

    public string? Fingerprint { get; init; }

    public string? Search { get; init; }

    public string? Sort { get; init; }

    public int? Limit { get; init; }

    public int? Offset { get; init; }

    public GatewayErrorQuery ToQuery() => new GatewayErrorQuery
    {
        From = From,
        To = To,
        MinimumLevel = GatewayLogLevels.ParseFilter(Level),
        ModelId = ModelId,
        StatusCode = Status,
        EventCode = Code,
        TenantId = TenantId,
        RequestId = RequestId,
        Fingerprint = Fingerprint,
        Search = Search,
        Sort = GatewayErrorQuery.ParseSort(Sort),
        Limit = Limit ?? GatewayErrorQuery.DefaultLimit,
        Offset = Offset ?? 0,
    }.Clamp();
}
