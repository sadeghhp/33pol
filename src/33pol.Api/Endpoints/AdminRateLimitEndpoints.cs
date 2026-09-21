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
        group.MapGet("/usage/timeseries", GetUsageSeries);
        group.MapGet("/history", GetHistoryAsync);
        group.MapGet("/schedule", GetSchedule);
        group.MapPost("/schedule/preview", PreviewSchedule);
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

        if (!TryResolveScheduleWindow(now, at, atLocal, timeZone, from, to, out var window, out var error))
        {
            return Results.BadRequest(new { message = error });
        }

        return Results.Json(service.GetSchedule(window.At, window.From, window.To, take ?? 50));
    }

    /// <summary>
    /// The instant and range a schedule report is built over, resolved from the parameters both the
    /// stored and the preview route accept. Shared so the two cannot drift into disagreeing about
    /// what a valid range is — the preview exists to answer the same question about a different
    /// rule set, and would be worth little if it also answered over a different window.
    /// </summary>
    private static bool TryResolveScheduleWindow(
        DateTimeOffset now,
        DateTimeOffset? at,
        string? atLocal,
        string? timeZone,
        DateTimeOffset? from,
        DateTimeOffset? to,
        out (DateTimeOffset At, DateTimeOffset From, DateTimeOffset To) window,
        out string? error)
    {
        window = default;
        error = null;

        var reference = at ?? now;
        if (!string.IsNullOrWhiteSpace(atLocal) &&
            !TryResolveLocal(atLocal, timeZone, out reference, out error))
        {
            return false;
        }

        var rangeFrom = from ?? now;
        var rangeTo = to ?? rangeFrom.AddDays(7);
        if (rangeTo <= rangeFrom)
        {
            error = "to must be after from.";
            return false;
        }

        if (rangeTo - rangeFrom > TimeSpan.FromDays(62))
        {
            error = "The calendar range may not exceed 62 days.";
            return false;
        }

        window = (reference, rangeFrom, rangeTo);
        return true;
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
    /// The schedule report for a rule set the caller has staged but not saved. Pure computation over
    /// the submitted rules; nothing is persisted and the stored configuration is not read.
    /// </summary>
    /// <remarks>
    /// The rules are held to exactly what a save would hold them to, and a set that would be refused
    /// is refused here with the same message. That is the point rather than a side effect: an
    /// operator learns their draft is invalid while the drawer that owns the mistake is still open,
    /// instead of at the save that closed it.
    /// </remarks>
    private static IResult PreviewSchedule(
        IRateLimitConfigAdminService service,
        TimeProvider? timeProvider,
        [FromBody] AdminRateLimitSchedulePreviewDto? request)
    {
        if (request is null)
        {
            return Results.BadRequest(new { message = "Request body is required." });
        }

        var submitted = request.Rules ?? [];

        // TryValidateRules below enforces this too; checking first only avoids materialising a set
        // that is already too large to accept. Same wording deliberately, so the preview and the
        // save cannot describe one refusal two ways.
        if (submitted.Count > RateLimitConfigValidation.MaxRules)
        {
            return Results.BadRequest(new
            {
                message = $"rules may not exceed {RateLimitConfigValidation.MaxRules} entries.",
            });
        }

        var rules = submitted
            .Where(static r => r is not null)
            .Select(static r => r.ToDefinition())
            .ToArray();

        if (!RateLimitConfigValidation.TryValidateRules(rules, out var ruleError))
        {
            return Results.BadRequest(new { message = ruleError });
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (!TryResolveScheduleWindow(
                now, request.At, request.AtLocal, request.TimeZone, request.From, request.To,
                out var window,
                out var error))
        {
            return Results.BadRequest(new { message = error });
        }

        return Results.Json(service.GetSchedule(rules, window.At, window.From, window.To, request.Take ?? 50));
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

    /// <summary>
    /// Per-minute history from the usage counters: gateway-wide, or for one configured limit.
    /// </summary>
    /// <remarks>
    /// <c>minutes</c> is 1–180 (default 60) and <c>bucketMinutes</c> 1–60 (default 1); out-of-range
    /// values are refused rather than clamped, because a chart drawn over a different range than the
    /// one asked for is wrong in a way nobody would notice. <c>limitId</c> is a limit id from the
    /// usage report; <c>anonymousBucket=true</c> selects a model rule's anonymous bucket. An unknown
    /// limit is 404, which is different from a known one with no traffic (200, zeros).
    /// </remarks>
    private static IResult GetUsageSeries(
        IRateLimitUsageTracker? tracker,
        TimeProvider? timeProvider,
        [FromQuery] int? minutes,
        [FromQuery] int? bucketMinutes,
        [FromQuery] string? limitId,
        [FromQuery] bool? anonymousBucket)
    {
        if (tracker is null)
        {
            return Results.Json(
                new { message = "Rate-limit usage tracking is not enabled in this deployment." },
                statusCode: 503);
        }

        var window = minutes ?? 60;
        var width = bucketMinutes ?? 1;
        if (window is < 1 or > 180)
        {
            return Results.BadRequest(new { message = "minutes must be between 1 and 180." });
        }

        if (width is < 1 or > 60)
        {
            return Results.BadRequest(new { message = "bucketMinutes must be between 1 and 60." });
        }

        if (limitId is { Length: > 512 })
        {
            return Results.BadRequest(new { message = "limitId is too long." });
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var id = string.IsNullOrWhiteSpace(limitId) ? null : limitId.Trim();
        var series = tracker.BuildSeries(window, width, id, anonymousBucket ?? false, now);

        return series is null
            ? Results.NotFound(new { message = "No activity has been recorded for that limit." })
            : Results.Json(series);
    }

    /// <summary>
    /// Rate-limit changes and refused attempts from the admin audit trail, newest first.
    /// </summary>
    /// <remarks>
    /// <c>take</c> is 1–100 (default 20). <c>before</c> pages backwards: pass the previous page's
    /// <c>nextBefore</c>. Read from the same file every other admin action is recorded in — this is
    /// a filtered view of it, not a second history.
    /// </remarks>
    private static async Task<IResult> GetHistoryAsync(
        IAuditLogReader? reader,
        [FromQuery] int? take,
        [FromQuery] DateTimeOffset? before,
        CancellationToken cancellationToken)
    {
        if (reader is null || !reader.IsAvailable)
        {
            return Results.Json(new AdminRateLimitHistoryDto { Available = false });
        }

        var limit = Math.Clamp(take ?? 20, 1, 100);
        var read = await reader
            .ReadRecentAsync(new AuditLogQuery(limit, "rate_limits.", before), cancellationToken)
            .ConfigureAwait(false);

        var entries = read.Entries.Select(AdminRateLimitHistoryEntryDto.FromAudit).ToArray();
        return Results.Json(new AdminRateLimitHistoryDto
        {
            Available = true,
            Entries = entries,
            HasMore = read.HasMore,
            NextBefore = read.HasMore && entries.Length > 0 ? entries[^1].TimestampUtc : null,
            ScanLimitReached = read.ScanLimitReached,
        });
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
        var dto = ToDto(current);
        var availability = service.GetWriteAvailability();
        dto.Writable = availability.Writable;
        dto.ReadOnlyReason = availability.ReasonCode;
        return Task.FromResult(Results.Json(dto));
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
        var stored = service.GetCurrent();
        var changes = DescribeChanges(stored.Rules, rules);
        var changedRules = changes?.Select(static c => c.Item).ToArray();

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
                new AuditLogEntry(
                    tenantId,
                    apiKeyId,
                    new { result.StatusCode, result.Message, BasedOnVersion = expectedVersion }));

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
                    Changes = changes?.Select(static c => c.Text).ToArray(),
                    ChangedRules = changedRules,
                    result.Version,
                    BasedOnVersion = expectedVersion,
                    PreviousVersion = stored.Version,
                }));

        // The version the write produced, in both places a client looks. Without it the only way
        // to learn what to base the next write on was a second request, and when that request
        // failed the client's next save conflicted with its own previous one.
        if (result.Version is long version)
        {
            httpContext.Response.Headers.ETag = FormatETag(version);
        }

        return Results.Json(new { message = result.Message, version = result.Version });
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
    private static IReadOnlyList<(string Text, AdminRateLimitRuleChangeDto Item)>? DescribeChanges(
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

        var changes = new List<(string Text, AdminRateLimitRuleChangeDto Item)>();

        // The same fact twice on purpose. The sentence is what someone grepping the file reads and
        // what existing queries match; the record is what the console joins to a rule by id, so it
        // never has to take a sentence apart to find out which rule it was about.
        void Add(string text, string kind, string identity, RateLimitRuleDefinition? old, RateLimitRuleDefinition? rule) =>
            changes.Add((text, new AdminRateLimitRuleChangeDto
            {
                Kind = kind,
                RuleId = identity.ToLowerInvariant(),
                Before = old is null ? null : Describe(old),
                After = rule is null ? null : Describe(rule),
            }));

        foreach (var (identity, rule) in after)
        {
            if (!before.TryGetValue(identity, out var old))
            {
                Add($"added {identity} = {Describe(rule)}", "added", identity, null, rule);
            }
            else if (!SameTier(old, rule) || old.Windows.Count != rule.Windows.Count)
            {
                Add($"changed {identity}: {Describe(old)} -> {Describe(rule)}", "changed", identity, old, rule);
            }
        }

        foreach (var (identity, old) in before)
        {
            if (!after.ContainsKey(identity))
            {
                Add($"removed {identity} (was {Describe(old)})", "removed", identity, old, null);
            }
        }

        changes.Sort(static (a, b) => string.CompareOrdinal(a.Text, b.Text));
        return changes;
    }

    private static bool SameTier(RateLimitRuleDefinition a, RateLimitRuleDefinition b) =>
        a.Rpm == b.Rpm && a.Burst == b.Burst && a.MaxConcurrentStreams == b.MaxConcurrentStreams
        // Switching a rule off stops it being enforced, which is as much a change as any number.
        && a.Enabled == b.Enabled;

    private static string Describe(RateLimitRuleDefinition rule) =>
        $"{rule.Rpm}rpm+{rule.Burst}burst/{rule.MaxConcurrentStreams}streams"
        + (rule.Windows.Count > 0 ? $" +{rule.Windows.Count}w" : string.Empty)
        + (rule.Enabled ? string.Empty : " off");

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
