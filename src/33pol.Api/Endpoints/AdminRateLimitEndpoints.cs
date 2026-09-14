using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;

namespace Pol33.Api.Endpoints;

public static class AdminRateLimitEndpoints
{
    public static IEndpointRouteBuilder MapAdminRateLimitEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/api/rate-limits")
            .RequireAuthorization(GatewayAuthPolicies.Operator);

        group.MapGet("/", GetAsync);
        group.MapPut("/", PutAsync);
        group.MapGet("/usage", GetUsageAsync);
        group.MapGet("/schedule", GetSchedule);
        group.MapPost("/windows/preview", PreviewWindow);

        return endpoints;
    }

    /// <summary>
    /// The schedule report: what every rule enforces at one instant, every window occurrence in a
    /// range, and when each rule's tier changes. Read straight from the stored configuration.
    /// </summary>
    /// <remarks>
    /// <c>at</c> is the instant to evaluate at (default: now); <c>atLocal</c> plus <c>timeZone</c>
    /// is the same thing spelled as a wall-clock time in a zone, which is how an operator types
    /// it. <c>from</c>/<c>to</c> bound the calendar (default: the next seven days). <c>take</c> caps
    /// the transitions list; the response says how many there were in total.
    /// </remarks>
    private static IResult GetSchedule(
        IRateLimitConfigAdminService service,
        TimeProvider? timeProvider,
        [FromQuery] DateTimeOffset? at,
        [FromQuery] string? atLocal,
        [FromQuery] string? timeZone,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? take)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        var reference = at ?? now;
        if (!string.IsNullOrWhiteSpace(atLocal))
        {
            if (!TryResolveLocal(atLocal, timeZone, out reference, out var error))
            {
                return Results.BadRequest(new { message = error });
            }
        }

        var rangeFrom = from ?? now;
        var rangeTo = to ?? rangeFrom.AddDays(7);
        if (rangeTo <= rangeFrom)
        {
            return Results.BadRequest(new { message = "to must be after from." });
        }

        if (rangeTo - rangeFrom > TimeSpan.FromDays(62))
        {
            return Results.BadRequest(new { message = "The calendar range may not exceed 62 days." });
        }

        return Results.Json(service.GetSchedule(reference, rangeFrom, rangeTo, take ?? 50));
    }

    /// <summary>A wall-clock time in a zone (<c>yyyy-MM-ddTHH:mm</c>) as an instant.</summary>
    private static bool TryResolveLocal(string local, string? timeZone, out DateTimeOffset instant, out string? error)
    {
        instant = default;
        error = null;

        if (!DateTime.TryParse(
                local,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            error = "atLocal must be a date and time, yyyy-MM-ddTHH:mm.";
            return false;
        }

        if (!RateLimitScheduleEvaluator.TryResolveTimeZone(timeZone, out var zone))
        {
            error = $"time zone '{timeZone}' is not known on this host.";
            return false;
        }

        var unspecified = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        instant = new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
        return true;
    }

    /// <summary>
    /// What a window an operator is composing would do, before it is saved. Pure computation over
    /// the submitted rule; nothing is persisted.
    /// </summary>
    private static IResult PreviewWindow(
        IRateLimitConfigAdminService service,
        [FromBody] AdminRateLimitWindowPreviewDto? request)
    {
        if (request is null)
        {
            return Results.BadRequest(new { message = "Request body is required." });
        }

        // Bounded before anything is built from it. The preview compares every window against every
        // other one, so an unbounded array is quadratic work the caller chooses the size of; the same
        // ceiling a saved rule is held to applies here, and the answer for a larger set is the same
        // refusal a save would give.
        if (request.Windows.Count > RateLimitConfigValidation.MaxWindowsPerRule)
        {
            return Results.BadRequest(new
            {
                message = $"A rule may not have more than {RateLimitConfigValidation.MaxWindowsPerRule} windows.",
            });
        }

        var rule = new RateLimitRuleDefinition(
            RateLimitScopeNames.Canonical(request.Scope),
            request.Target ?? string.Empty,
            request.Rpm,
            request.Burst,
            request.MaxConcurrentStreams)
        {
            Schedule = request.Windows.Where(static w => w is not null).Select(static w => w.ToDefinition()).ToArray(),
        };

        return Results.Json(service.PreviewWindow(rule, request.Candidate?.Trim() ?? string.Empty));
    }

    /// <summary>
    /// The usage report: who is sending what, against which limits, and where those limits are being
    /// hit.
    /// </summary>
    /// <remarks>
    /// A read of in-memory counters, so it is cheap enough to poll and safe to call during an
    /// incident — it takes no database connection and touches nothing on the request path. The
    /// window is capped at what the counters actually hold; asking for more returns the longest
    /// available rather than an error, and the response says which window it answered.
    /// </remarks>
    private static IResult GetUsageAsync(
        IRateLimitUsageTracker? tracker,
        TimeProvider? timeProvider,
        [FromQuery] int? minutes,
        [FromQuery] int? take)
    {
        if (tracker is null)
        {
            return Results.Json(
                new { message = "Rate-limit usage tracking is not enabled in this deployment." },
                statusCode: 503);
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return Results.Json(tracker.BuildReport(minutes ?? 60, take ?? 25, now));
    }

    /// <remarks>
    /// The configuration version goes out as a weak <c>ETag</c>. A client that sends it back as
    /// <c>If-Match</c> on the PUT is telling the gateway which version its change is based on, which is
    /// what lets a stale write be refused instead of erasing whatever landed in between.
    /// </remarks>
    private static Task<IResult> GetAsync(
        HttpContext httpContext,
        IRateLimitConfigAdminService service,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var current = service.GetCurrent();
        httpContext.Response.Headers.ETag = FormatETag(current.Version);
        return Task.FromResult(Results.Json(ToDto(current)));
    }

    /// <summary>Weak, because the body is a JSON projection rather than a byte-exact resource.</summary>
    private static string FormatETag(long version) => $"W/\"{version}\"";

    /// <summary>
    /// The version an <c>If-Match</c> names, or null when the header is absent or is <c>*</c>.
    /// </summary>
    /// <remarks>
    /// <c>*</c> means "any current version", which is the unconditional write a client gets by sending
    /// no header at all. A header that is present but unparseable is <em>not</em> treated as absent —
    /// silently upgrading a failed precondition to an unconditional write is the one reading of it
    /// that loses data — so the caller reports it instead.
    /// </remarks>
    private static bool TryReadIfMatch(HttpContext httpContext, out long? expectedVersion, out string? error)
    {
        expectedVersion = null;
        error = null;

        var header = httpContext.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header) || header == "*")
        {
            return true;
        }

        var value = header.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.Trim('"');

        if (!long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            error = "If-Match must be the ETag returned by GET /admin/api/rate-limits, or '*'.";
            return false;
        }

        expectedVersion = parsed;
        return true;
    }

    private static async Task<IResult> PutAsync(
        HttpContext httpContext,
        IRateLimitConfigAdminService service,
        IAuditLogger audit,
        [FromBody] AdminRateLimitsDto? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { message = "Request body is required." });
        }

        if (!TryReadIfMatch(httpContext, out var expectedVersion, out var preconditionError))
        {
            return Results.BadRequest(new { message = preconditionError });
        }

        // Null rules pass through as null so the service leaves the stored set alone; an empty list
        // is a deliberate "delete them all" and is passed through as such.
        var rules = request.Rules?.Select(static r => r.ToDefinition()).ToArray();

        // Diffed against the stored set before the write, while both sides are still available. The
        // audit entry used to carry only counts, and because a rule set is replaced wholesale an
        // unchanged count says nothing about whether anything changed — so "why was this tenant
        // unlimited last Tuesday" had no answer in the trail.
        var changes = DescribeChanges(service.GetCurrent().Rules, rules);

        var result = await service
            .UpdateAsync(
                request.Enabled,
                request.AdaptiveEnabled,
                request.Default,
                request.Plans,
                rules,
                expectedVersion,
                cancellationToken)
            .ConfigureAwait(false);

        var tenantId = httpContext.User.FindFirst(GatewayAuthClaims.TenantId)?.Value;
        var apiKeyId = httpContext.User.FindFirst(GatewayAuthClaims.ApiKeyId)?.Value;

        if (!result.Success)
        {
            // Refusals are audited too. A rejected write is an attempt to change a security-relevant
            // control, and a trail that records only what succeeded cannot show one.
            audit.LogAdminAction(
                "rate_limits.update_refused",
                new AuditLogEntry(tenantId, apiKeyId, new { result.StatusCode, result.Message }));

            return Results.Json(new { message = result.Message }, statusCode: result.StatusCode);
        }

        audit.LogAdminAction(
            "rate_limits.update",
            new AuditLogEntry(
                tenantId,
                apiKeyId,
                new
                {
                    request.Enabled,
                    request.AdaptiveEnabled,
                    request.Default.Rpm,
                    request.Default.Burst,
                    request.Default.MaxConcurrentStreams,
                    PlanCount = request.Plans.Count,
                    RuleCount = rules?.Length,
                    WindowCount = rules?.Sum(static r => r.Windows.Count),
                    Changes = changes,
                }));

        return Results.Json(new { message = result.Message });
    }

    /// <summary>
    /// Which rules this write adds, removes or retiers, as short strings an audit reader can scan.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="RateLimitConfigValidation.MaxRules"/> on both sides, and each entry is a
    /// scope, a target and two tiers — so the whole field stays small enough to live in an audit
    /// record. Null when the caller sent no rule list at all, which means "leave them alone" and is
    /// not a change. Window counts are reported rather than the windows themselves: a schedule diff
    /// would dominate the entry, and the rule's identity is what an investigation starts from.
    /// </remarks>
    private static IReadOnlyList<string>? DescribeChanges(
        IReadOnlyList<RateLimitRuleDefinition> stored,
        IReadOnlyList<RateLimitRuleDefinition>? submitted)
    {
        if (submitted is null)
        {
            return null;
        }

        var before = stored.ToDictionary(static r => r.Identity, StringComparer.OrdinalIgnoreCase);
        var after = submitted
            .Where(static r => r is not null)
            .GroupBy(static r => r.Identity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);

        var changes = new List<string>();

        foreach (var (identity, rule) in after)
        {
            if (!before.TryGetValue(identity, out var old))
            {
                changes.Add($"added {identity} = {Describe(rule)}");
            }
            else if (!SameTier(old, rule) || old.Windows.Count != rule.Windows.Count)
            {
                changes.Add($"changed {identity}: {Describe(old)} -> {Describe(rule)}");
            }
        }

        foreach (var (identity, old) in before)
        {
            if (!after.ContainsKey(identity))
            {
                changes.Add($"removed {identity} (was {Describe(old)})");
            }
        }

        changes.Sort(StringComparer.Ordinal);
        return changes;
    }

    private static bool SameTier(RateLimitRuleDefinition a, RateLimitRuleDefinition b) =>
        a.Rpm == b.Rpm && a.Burst == b.Burst && a.MaxConcurrentStreams == b.MaxConcurrentStreams;

    private static string Describe(RateLimitRuleDefinition rule) =>
        $"{rule.Rpm}rpm+{rule.Burst}burst/{rule.MaxConcurrentStreams}streams"
        + (rule.Windows.Count > 0 ? $" +{rule.Windows.Count}w" : string.Empty);

    private static AdminRateLimitsDto ToDto(Core.Configuration.RateLimitAdminConfig config) =>
        new()
        {
            Version = config.Version,
            Enabled = config.Enabled,
            AdaptiveEnabled = config.AdaptiveEnabled,
            Default = new Core.Configuration.RateLimitTierOptions
            {
                Rpm = config.Default.Rpm,
                Burst = config.Default.Burst,
                MaxConcurrentStreams = config.Default.MaxConcurrentStreams,
            },
            Plans = config.Plans.ToDictionary(
                static p => p.Key,
                static p => new Core.Configuration.RateLimitTierOptions
                {
                    Rpm = p.Value.Rpm,
                    Burst = p.Value.Burst,
                    MaxConcurrentStreams = p.Value.MaxConcurrentStreams,
                },
                StringComparer.OrdinalIgnoreCase),
            Rules = [.. config.Rules.Select(AdminRateLimitRuleDto.FromDefinition)],
        };
}
