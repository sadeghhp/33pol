using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Errors;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Parsing;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Middleware;

/// <summary>
/// Admits or refuses an inference request against every rate-limit scope that applies to it.
/// </summary>
/// <remarks>
/// <para><b>Scopes compose.</b> Global, tenant, key, model, tenant×model and key×model each hold
/// their own token bucket, and a request needs a token from every one that is configured. Adding a
/// narrower rule can only ever tighten what a caller may do, so the outcome does not depend on the
/// order an operator configured things in. The one place precedence exists is <em>inside</em> the
/// tenant scope, where a per-tenant override beats the tenant's plan, which beats the default.</para>
///
/// <para><b>Two stages, and why.</b> The model lives in the request body, and reading it means
/// buffering and parsing what the client sent. A caller already over its tenant or key budget must
/// not be able to make the gateway do that work: it would turn the cheapest thing a limiter does —
/// saying no — into the most expensive. So the model-independent scopes are evaluated first and gate
/// the parse; the model-dependent ones follow. If the second stage refuses, the tokens the first
/// stage took are handed back, so being blocked by a narrow model limit does not also burn the
/// caller's tenant-wide budget.</para>
///
/// <para>The parse is skipped entirely when no model-scoped rule is configured anywhere, which is
/// the default. A deployment that does not use per-model limits pays nothing for them.</para>
///
/// <para><b>Only granted models are charged.</b> The <c>model</c> bucket is shared by every caller
/// of that model, and grants are enforced downstream in the router. Charging the bucket before the
/// grant is checked meant any authenticated key could drain a model's gateway-wide budget with
/// requests it was always going to be refused — a tenant denying a model to every other tenant at
/// its own request rate. Stage two is therefore skipped for a caller the model is not granted to;
/// its identity scopes are still charged, so the attempts are not free, and the router still gives
/// the 403.</para>
/// </remarks>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRateLimitPlanResolver _planResolver;
    private readonly IDistributedRateLimitStore _rateLimitStore;
    private readonly IErrorResponseWriter _errors;
    private readonly IGatewayMetricsCollector _metrics;
    private readonly IModelRegistry _registry;
    private readonly IAdaptiveRateLimitGovernor? _governor;
    private readonly IRateLimitUsageTracker? _usage;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Answers "may this key use this model?" from an in-memory cache, so the check that keeps an
    /// ungranted caller off a shared model bucket costs two cache lookups on the hot path. Optional
    /// only so the tests that build this middleware by hand keep compiling; when either it or
    /// <see cref="_authState"/> is absent no grant check is made and every model scope is charged,
    /// which is the behaviour these two exist to correct.
    /// </summary>
    private readonly IModelGrantService? _modelGrants;

    private readonly IGatewayAuthenticationState? _authState;

    public RateLimitMiddleware(
        RequestDelegate next,
        IRateLimitPlanResolver planResolver,
        IDistributedRateLimitStore rateLimitStore,
        IErrorResponseWriter errors,
        IGatewayMetricsCollector metrics,
        IModelRegistry registry,
        IAdaptiveRateLimitGovernor? governor = null,
        IRateLimitUsageTracker? usage = null,
        TimeProvider? timeProvider = null,
        IModelGrantService? modelGrants = null,
        IGatewayAuthenticationState? authState = null)
    {
        _next = next;
        _planResolver = planResolver;
        _rateLimitStore = rateLimitStore;
        _errors = errors;
        _metrics = metrics;
        _registry = registry;
        _governor = governor;
        _usage = usage;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _modelGrants = modelGrants;
        _authState = authState;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!InferenceRouteClassifier.IsRoutableInference(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Global master switch, read from the live snapshot so the admin toggle applies without a
        // restart. With it off, nothing below runs — including the early answer for unroutable
        // bodies, which the router gives identically a few frames later.
        if (!_planResolver.IsEnabled())
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var subject = RateLimitPartition.ResolveSubject(context);
        var now = _timeProvider.GetUtcNow();

        // --- Stage one: the scopes that do not need the body. ---
        //
        // Kept in a synchronous helper because the rule set is a span over the cached plan, and a
        // span cannot live across an await. Holding it only inside a non-async frame is also what
        // guarantees no rule set outlives the decision it was built for.
        var tightest = AcquireIdentityScopes(subject, now);
        if (!tightest.IsAcquired)
        {
            await RejectAsync(context, subject, modelId: null, tightest, now).ConfigureAwait(false);
            return;
        }

        var modelId = (string?)null;

        if (_planResolver.HasModelScopedRules())
        {
            // The canonical id, not what the client typed: per-model rules are configured against
            // canonical ids, so matching on the raw name would let an alias walk straight past the
            // limit set for the model behind it.
            var model = await ResolveCanonicalModelAsync(context).ConfigureAwait(false);
            modelId = model?.Id;

            // Charged only for a model this caller may actually use. The grant is re-checked by the
            // router, which is what produces the 403; this one exists purely so an ungranted request
            // cannot spend a bucket shared with every other tenant.
            if (model is not null && await IsModelChargeableAsync(context, model).ConfigureAwait(false))
            {
                // --- Stage two: the scopes that do. ---
                var modelScopes = AcquireModelScopes(subject, model.Id, now);
                if (!modelScopes.IsAcquired)
                {
                    await RejectAsync(context, subject, model.Id, modelScopes, now).ConfigureAwait(false);
                    return;
                }

                tightest = Tighter(tightest, modelScopes);
            }
        }

        WriteBudgetHeaders(context, tightest);
        RecordAdmission(subject, modelId, tightest);

        // Answered here, after the debit, when the cached parse already says the router is going to
        // refuse this body: it saves the rest of the pipeline for a request that cannot be served.
        // The debit has to come first — answering ahead of it made an unroutable body a free request,
        // so a tenant could send malformed 25 MB payloads at any rate it liked and never be limited.
        if (await RejectUnroutableBodyAsync(context).ConfigureAwait(false))
        {
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>Takes a token from every scope that can be decided without the request body.</summary>
    private RateLimitAcquireResult AcquireIdentityScopes(in RateLimitSubject subject, DateTimeOffset now) =>
        _rateLimitStore.TryAcquireAll(_planResolver.Resolve(subject, modelId: null).IdentityRules, now);

    /// <summary>
    /// Takes a token from every model-scoped rule, refunding the first stage if one of them refuses.
    /// </summary>
    /// <remarks>
    /// The identity rules are re-resolved rather than carried in from the first stage: a resolved
    /// plan is cached, so this is a dictionary lookup returning the very same array, and it avoids
    /// holding a span across the await that sits between the two stages.
    /// </remarks>
    private RateLimitAcquireResult AcquireModelScopes(
        in RateLimitSubject subject,
        string modelId,
        DateTimeOffset now)
    {
        var modelRules = _planResolver.Resolve(subject, modelId).ModelRules;
        if (modelRules.Length == 0)
        {
            return RateLimitAcquireResult.Unlimited;
        }

        var result = _rateLimitStore.TryAcquireAll(modelRules, now);
        if (!result.IsAcquired)
        {
            // Hand back what the first stage took. Without it, a caller pinned by a narrow per-model
            // limit would still spend its tenant-wide budget on every attempt, so one throttled model
            // would eventually rate-limit that tenant everywhere.
            _rateLimitStore.RefundAll(_planResolver.Resolve(subject, modelId: null).IdentityRules, now);
        }

        return result;
    }

    /// <summary>
    /// The model this request asks for, resolved through the registry so an alias lands on the same
    /// entry the canonical id does, or null when the body is unparseable, names no model, or names
    /// one the registry does not know.
    /// </summary>
    /// <remarks>
    /// A null is not an error here. An unroutable body is answered a few lines later by
    /// <see cref="RejectUnroutableBodyAsync"/> or by the router, and an unknown model has no
    /// per-model rule to apply by definition — in both cases the request has already been charged
    /// against the scopes that do apply to it, which is the point: a caller cannot get free requests
    /// by naming a model that does not exist.
    /// </remarks>
    private async Task<ModelConfig?> ResolveCanonicalModelAsync(HttpContext context)
    {
        if (!InferenceRequestParseCache.TryGet(context, out var cached))
        {
            cached = await ParseBodyAsync(context).ConfigureAwait(false);
        }

        var requested = cached?.Model;
        if (string.IsNullOrWhiteSpace(requested))
        {
            return null;
        }

        return _registry.TryGetModel(requested, out var model) ? model : null;
    }

    /// <summary>
    /// Whether this caller's request may be charged against the model-scoped buckets — that is,
    /// whether the router is going to let it through the grant check rather than answering 403.
    /// </summary>
    /// <remarks>
    /// <para>The <c>model</c> scope's bucket is shared by every caller of that model. Charging it
    /// for a request that will be refused is a cross-tenant denial of service: one key can spend a
    /// model's entire gateway-wide budget on requests it has no access to, and every other tenant
    /// sees 429s for a model they are granted.</para>
    ///
    /// <para>The same conditions as the router's own check, so the two cannot disagree about who is
    /// granted what: grants only apply where authentication is required and the model is not public.
    /// A request carrying no usable identity is not chargeable either — the router refuses it, and
    /// treating it as chargeable would hand the same denial of service to unauthenticated traffic.
    /// The check is an in-memory cache lookup on all but the first request for a key.</para>
    /// </remarks>
    private async Task<bool> IsModelChargeableAsync(HttpContext context, ModelConfig model)
    {
        if (_modelGrants is null || _authState is null)
        {
            return true;
        }

        if (!_authState.IsAuthenticationRequired || model.AllowsPublicGatewayAccess())
        {
            return true;
        }

        if (context.User.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(context.User.FindFirstValue(GatewayAuthClaims.TenantId), out var tenantId) ||
            !Guid.TryParse(context.User.FindFirstValue(GatewayAuthClaims.ApiKeyId), out var apiKeyId))
        {
            return false;
        }

        return await _modelGrants
            .IsModelAllowedAsync(tenantId, apiKeyId, model.Id, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Buffers and parses the body once, publishing the result for the router so it never parses the
    /// same bytes twice.
    /// </summary>
    private static async Task<InferenceRequestInfo?> ParseBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        context.Request.Body.Position = 0;

        try
        {
            var info = await InferenceRequestParser
                .ParseAsync(context.Request.Body, context.RequestAborted)
                .ConfigureAwait(false);
            InferenceRequestParseCache.SetParsed(context, info);
            return info;
        }
        catch (JsonException)
        {
            InferenceRequestParseCache.SetInvalidJson(context);
            return null;
        }
        finally
        {
            // Guarded so a failure that aborted the body read (an oversized payload, above all)
            // propagates to the exception middleware instead of being masked by a seek that throws.
            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }
        }
    }

    /// <summary>
    /// The reading a client should pace itself against: the scope with the smallest share of its
    /// budget left. Reporting the roomier of two limits would have the client pacing against a
    /// budget that is not the one about to refuse it.
    /// </summary>
    private static RateLimitAcquireResult Tighter(RateLimitAcquireResult a, RateLimitAcquireResult b)
    {
        if (a.Limit is not { } limitA || limitA <= 0)
        {
            return b;
        }

        if (b.Limit is not { } limitB || limitB <= 0)
        {
            return a;
        }

        return (double)(b.Remaining ?? 0) / limitB < (double)(a.Remaining ?? 0) / limitA ? b : a;
    }

    /// <summary>
    /// Writes the same <c>invalid_json</c> / <c>missing_model</c> answer the router would, when the
    /// cached parse already says the body cannot be routed. Returns false when the body is routable
    /// or nothing is cached yet.
    /// </summary>
    /// <remarks>
    /// The parse is cached when <c>PublicModelDetectionMiddleware</c> ran a parse of its own (it does
    /// so only while at least one model is public), or when this middleware ran one to resolve the
    /// model for a per-model rule. With neither, nothing is cached and the router answers instead —
    /// same status, same body, one middleware later.
    /// </remarks>
    private async Task<bool> RejectUnroutableBodyAsync(HttpContext context)
    {
        if (!InferenceRequestParseCache.TryGet(context, out var cached))
        {
            return false;
        }

        if (cached is null)
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.InvalidJson),
                context.RequestAborted).ConfigureAwait(false);
            return true;
        }

        if (string.IsNullOrWhiteSpace(cached.Value.Model))
        {
            await context.WriteGatewayErrorAsync(
                _errors.Write(GatewayErrorCode.MissingModel),
                context.RequestAborted).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Publishes the partition's remaining budget on every answer, admitted or not, so a client can
    /// pace itself instead of discovering the limit by being refused.
    /// </summary>
    private static void WriteBudgetHeaders(HttpContext context, RateLimitAcquireResult acquire)
    {
        if (acquire.Limit is not { } limit)
        {
            return;
        }

        var headers = context.Response.Headers;
        headers[GatewayHeaders.RateLimitLimit] = limit.ToString();
        headers[GatewayHeaders.RateLimitRemaining] = (acquire.Remaining ?? 0).ToString();
        headers[GatewayHeaders.RateLimitReset] = (acquire.ResetAfterSeconds ?? 0).ToString();

        if (acquire.Scope is { } scope)
        {
            // Several limits apply at once, so a bare remaining-count is ambiguous: this says which
            // of them the number belongs to.
            headers[GatewayHeaders.RateLimitScope] = scope.ToLabel();
        }

        if (acquire.AdaptiveFactor < 1.0 && acquire.ConfiguredRpm > 0)
        {
            // The two rates the governor moved between, read straight off the rule rather than
            // reconstructed from the capacity: Scale() rounds rpm and burst independently, so
            // dividing the capacity by the factor does not invert it and the header was off by a
            // few whenever either rounding went the other way.
            headers[GatewayHeaders.RateLimitAdaptive] = $"{acquire.EffectiveRpm}/{acquire.ConfiguredRpm}";
        }
    }

    private async Task RejectAsync(
        HttpContext context,
        RateLimitSubject subject,
        string? modelId,
        RateLimitAcquireResult acquire,
        DateTimeOffset now)
    {
        WriteBudgetHeaders(context, acquire);

        var scope = acquire.Scope;
        var reason = acquire.Control == RateLimitControl.Concurrency
            ? "stream_concurrency:" + (scope?.ToLabel() ?? "tenant")
            : "rate_limit:" + (scope?.ToLabel() ?? "tenant");

        _metrics.RecordRateLimitRejection(reason, subject.PartitionKey, modelId);
        _governor?.RecordOutcome(subject.PartitionKey, admitted: false, now);
        _usage?.Record(new RateLimitUsageEvent(
            subject.PartitionKey,
            subject.ApiKeyId,
            modelId,
            Admitted: false,
            scope,
            acquire.Control,
            acquire.ConfiguredRpm,
            acquire.EffectiveRpm));

        var code = acquire.RejectionReason switch
        {
            GatewayRateLimitReason.ConcurrencyLimitExceeded => GatewayErrorCode.ConcurrencyLimitExceeded,
            _ => GatewayErrorCode.RateLimitExceeded,
        };

        // The bucket says how long until the next token; the governor may lengthen that for a client
        // that keeps coming back, and jitters it so a crowd refused together does not return
        // together.
        var retryAfter = acquire.RetryAfterSeconds ?? 1;
        if (_governor is not null)
        {
            retryAfter = _governor.GetRetryAfterSeconds(subject.PartitionKey, retryAfter, now);
        }

        await context.WriteGatewayErrorAsync(
            _errors.Write(code),
            context.RequestAborted,
            retryAfter).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the admission for the usage report — but deliberately not for the governor.
    /// </summary>
    /// <remarks>
    /// An admission here is provisional. A streaming request still faces the concurrency cap in the
    /// router, and clearing the partition's retry backoff at this point would undo, a few frames
    /// early, the rejection the router is about to record: the counter would go to zero on every
    /// attempt and a client pinned on a concurrency cap could never escalate past one consecutive
    /// rejection. The router owns the governor's admitted signal because it is the last gate.
    /// </remarks>
    private void RecordAdmission(
        RateLimitSubject subject,
        string? modelId,
        RateLimitAcquireResult acquire)
    {
        _usage?.Record(new RateLimitUsageEvent(
            subject.PartitionKey,
            subject.ApiKeyId,
            modelId,
            Admitted: true,
            acquire.Scope,
            acquire.Control,
            acquire.ConfiguredRpm,
            acquire.EffectiveRpm));
    }
}
