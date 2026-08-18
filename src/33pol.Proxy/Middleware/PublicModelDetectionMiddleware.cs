using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Proxy.Parsing;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Middleware;

/// <summary>
/// Marks inference requests addressed to a public model, so authentication can let them through
/// without a key. Runs ahead of authentication and rate limiting because it has to.
/// </summary>
/// <remarks>
/// That position means every routable inference POST reaches body buffering and a JSON parse before
/// any credential is checked or any per-IP limit applied. The parse is therefore gated on the one
/// thing that makes it useful: at least one registered model allows public access. With none, no
/// request can be public, the flags this middleware sets can never be set, and the body is left
/// untouched for the router to parse <em>after</em> authentication and rate limiting have run.
/// The router still gets its single parse; it simply happens later.
///
/// "At least one public model" is answered from a short-lived cache: the registry hands out cloned
/// lists, which is fine for the admin API but not for a per-request check on the hot path. A model
/// flipped to public is seen within <see cref="PublicModelCacheTtl"/>.
/// </remarks>
public sealed class PublicModelDetectionMiddleware
{
    /// <summary>How long the "does any public model exist" answer is reused.</summary>
    public static readonly TimeSpan PublicModelCacheTtl = TimeSpan.FromSeconds(1);

    private readonly RequestDelegate _next;
    private readonly IModelRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly object _cacheLock = new();
    private long _cacheExpiresAtTicks;
    private bool _cachedAnyPublicModel;

    public PublicModelDetectionMiddleware(RequestDelegate next, IModelRegistry registry)
        : this(next, registry, TimeProvider.System)
    {
    }

    public PublicModelDetectionMiddleware(RequestDelegate next, IModelRegistry registry, TimeProvider timeProvider)
    {
        _next = next;
        _registry = registry;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!InferenceRouteClassifier.IsRoutableInference(context) || !AnyPublicModelRegistered())
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering();

        // Parse from the start of the body unconditionally: the byte offsets the parser reports are
        // relative to where it began reading, and every consumer of them seeks back to 0.
        context.Request.Body.Position = 0;

        try
        {
            var requestInfo = await InferenceRequestParser
                .ParseAsync(context.Request.Body, context.RequestAborted)
                .ConfigureAwait(false);

            // Published for the router, which needs the same three scalars and must not pay for a
            // second parse of the same body.
            InferenceRequestParseCache.SetParsed(context, requestInfo);

            if (!string.IsNullOrWhiteSpace(requestInfo.Model) &&
                _registry.TryGetModel(requestInfo.Model, out var modelConfig) &&
                modelConfig is not null &&
                modelConfig.AllowsPublicGatewayAccess())
            {
                context.Items[PublicModelAccessKeys.IsPublicInference] = true;
                context.Items[PublicModelAccessKeys.CanonicalModelId] = modelConfig.Id;
            }
        }
        catch (JsonException)
        {
            // Leave items unset; auth and router enforce keys / return invalid JSON. Recording the
            // failure keeps the router from re-parsing a body already known to be malformed.
            InferenceRequestParseCache.SetInvalidJson(context);
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

        await _next(context).ConfigureAwait(false);
    }

    private bool AnyPublicModelRegistered()
    {
        var now = _timeProvider.GetTimestamp();
        lock (_cacheLock)
        {
            if (now < _cacheExpiresAtTicks)
            {
                return _cachedAnyPublicModel;
            }
        }

        var any = false;
        foreach (var model in _registry.GetAllModels())
        {
            if (model.AllowsPublicGatewayAccess())
            {
                any = true;
                break;
            }
        }

        lock (_cacheLock)
        {
            _cachedAnyPublicModel = any;
            _cacheExpiresAtTicks = now + (long)(PublicModelCacheTtl.TotalSeconds * _timeProvider.TimestampFrequency);
        }

        return any;
    }
}
