using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Pol33.Api.DependencyInjection;
using Pol33.Api.Endpoints;
using Pol33.App.Metrics;
using Pol33.Core.Configuration;
using Pol33.Observability.Metrics;
using Pol33.Proxy.DependencyInjection;
using Pol33.Security.DependencyInjection;
using Serilog;

namespace Pol33.App;

public static class GatewayHostBuilderExtensions
{
    public static WebApplicationBuilder ConfigureGatewayHost(this WebApplicationBuilder builder)
    {
        // writeToProviders is load-bearing, not a preference. Without it Serilog swaps in a logger
        // factory whose AddProvider is a no-op, so the admin log sink registered in the container is
        // constructed and then never called — which is why the admin Logs tab showed nothing but the
        // handful of entries written to the store directly.
        // Serilog owns console output. Clearing the default providers first stops writeToProviders
        // below from also feeding the built-in console logger, which would print every line twice.
        builder.Logging.ClearProviders();

        builder.Host.UseSerilog(
            (context, services, configuration) => configuration.ReadFrom.Configuration(context.Configuration),
            writeToProviders: true);

        builder.WebHost.ConfigureKestrel((context, options) =>
        {
            options.AllowSynchronousIO = false;
            options.AddServerHeader = false;

            var gatewayOptions = context.Configuration
                .GetSection(GatewayOptions.SectionName)
                .Get<GatewayOptions>() ?? new GatewayOptions();
            options.Limits.MaxRequestBodySize = gatewayOptions.Resilience.MaxRequestBodyBytes;

            // Previously left at the framework default and not exposed, so the "data arriving too
            // slowly" rejections it produced could be neither sized nor tuned. 0 disables it.
            var resilience = gatewayOptions.Resilience;
            options.Limits.MinRequestBodyDataRate = resilience.MinRequestBodyBytesPerSecond > 0
                ? new MinDataRate(
                    resilience.MinRequestBodyBytesPerSecond,
                    TimeSpan.FromSeconds(Math.Max(1, resilience.MinRequestBodyDataRateGraceSeconds)))
                : null;
        });

        builder.Services.AddResponseCompression(options =>
        {
            // Every admin asset was previously served uncompressed: a signed-in visit cost ~925 KB,
            // some 650 KB of it text that had never been encoded at all (measured — see
            // perf/frontend/baseline/2026-09-14-afeb6e0-pre-m1-cache.json). Brotli is listed first so
            // a modern browser gets it and gzip only covers the stragglers.
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();

            // On by default this is off, because compressing a secret-bearing response whose content
            // an attacker can partly control is the BREACH precondition. It does not apply here: the
            // admin API authenticates with an X-API-Key header rather than an ambient cookie, so a
            // cross-origin page cannot make the browser issue an authenticated request in the first
            // place, and `connect-src 'self'` plus `frame-ancestors 'none'` (AdminSecurityHeaders)
            // close the paths that would let one observe the sizes.
            options.EnableForHttps = true;

            // An explicit list rather than ResponseCompressionDefaults: the defaults omit
            // text/javascript and image/svg+xml, and leaving the set implicit is how a streaming
            // content type quietly acquires a compressor in a future framework version.
            options.MimeTypes =
            [
                "text/html",
                "text/css",
                "text/javascript",
                "application/javascript",
                "application/json",
                "image/svg+xml",
            ];

            // Load-bearing, and the one way this change could break production. Compressing the
            // admin live feed would hold frames in the compressor's buffer instead of flushing them,
            // so the Overview's push stream would stall and fall back to 2 s polling — or appear to
            // work and then go silent. text/event-stream is absent from MimeTypes above, so this is
            // belt and braces; it is stated explicitly because it must survive anyone editing that
            // list. Guarded by AdminAssetCachingTests.LiveStream_IsNotCompressed.
            // woff2 is deliberately absent too: it is already compressed, so a second pass costs CPU
            // and returns nothing.
            options.ExcludedMimeTypes = ["text/event-stream"];
        });

        return builder;
    }

    public static WebApplication ConfigureGatewayPipeline(this WebApplication app)
    {
        // First in the pipeline: everything downstream — request logging, the audit trail, and above
        // all the anonymous rate-limit partition — reads the remote address, and each would otherwise
        // record the proxy instead of the caller.
        app.UseGatewayForwardedHeaders();

        app.UseSerilogRequestLogging(options =>
        {
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestMethod", httpContext.Request.Method);
                diagnosticContext.Set("RequestPath", httpContext.Request.Path.Value ?? string.Empty);
                diagnosticContext.Set("StatusCode", httpContext.Response.StatusCode);
            };
        });

        // Inside request logging so Serilog records the status this handler settled on rather than
        // the exception, and outside everything else so no unhandled failure can reach Kestrel and be
        // answered with a bare status line instead of the documented error body.
        // The request id goes on first, so the exception handler's own log line and the error
        // record it writes carry the same id as everything else on the request — including
        // failures thrown by the middleware that used to sit between the two.
        app.UseGatewayRequestId();
        app.UseGatewayExceptionHandling();

        app.UseRouting();
        app.UseCors();

        // Ahead of PublicModelDetection, which is the first thing to call EnableBuffering() and
        // parse the body. Registered after it, this middleware's own body-size cap could never fire
        // in time: an unauthenticated request was already buffered (spilling to a temp file past
        // 30 KB) and fully JSON-parsed before the limit it exists to enforce was applied. The drain
        // check belongs here for the same reason — a shutting-down gateway should reject before it
        // spends work on the body.
        app.UseInferenceResilience();
        // Ahead of PublicModelDetection for the same reason PublicModelDetection is ahead of
        // authentication: it is the first middleware to buffer and JSON-parse the body, and an
        // anonymous caller already over its per-address budget should not make the gateway do that.
        app.UseAnonymousAdmissionGuard();
        app.UsePublicModelDetection();
        // Outside the security middleware, so the requests it refuses are counted somewhere: the
        // rate limiter proper runs behind authentication and never sees a rejected credential.
        app.UseAuthFailureRateLimiting();
        app.UseGatewaySecurity(app.Configuration);
        // /metrics is an anonymous path to the authentication handler (probes and scrapers do not
        // carry gateway keys), so its gate lives here: scrape token, Operator key, or explicit opt-in.
        app.UseMetricsScrapeAuthorization();

        app.MapGet("/", GatewayEndpoints.GetRoot);
        app.MapGet("/admin", () => Results.Redirect("/admin/index.html"));
        app.MapHealthChecks("/health/live");
        app.MapConfigAdminEndpoints();
        app.MapAdminRateLimitEndpoints();
        app.MapAdminCorsEndpoints();
        app.MapAdminKeyEndpoints();
        app.MapAdminModelGrantEndpoints();
        app.MapAdminControlPlaneEndpoints();
        app.MapAdminErrorEndpoints();
        app.MapAdminProviderEndpoints();
        app.MapAdminUsageEndpoints();
        app.MapAdminOverviewEndpoints();
        app.MapMaintenanceAdminEndpoints();
        app.MapModelsEndpoints();
        // Ahead of the static-file handler so the console's assets are compressed, and scoped to
        // /admin rather than global. The scope is the load-bearing part: WebApplication runs the
        // terminal endpoint middleware after everything registered here, so an unscoped
        // UseResponseCompression would also wrap the inference data path — spending CPU on a proxy
        // whose overhead is a measured design constraint (perf/k6/scripts/overhead-compare.js) for
        // no gain. An upstream that compresses is already passed through untouched, and a streamed
        // token chunk is far too small to compress, so per-chunk framing would be the only result.
        // UseWhen branches and rejoins, so /admin still reaches the same static-file and endpoint
        // middleware below; everything else reaches them without a compressor in the way.
        // Within the branch the compressor still wraps the admin API as well as the assets — which
        // is exactly why text/event-stream is excluded where it is registered.
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase),
            admin => admin.UseResponseCompression());
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                if (ctx.Context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyAdminCachePolicy(ctx);
                    // Outside the cache decision on purpose: every branch is still the admin console,
                    // and a header set for one kind of asset but not another is how a surface loses
                    // its CSP without anyone noticing.
                    AdminSecurityHeaders.Apply(ctx.Context.Response.Headers);
                }
            }
        });
        app.MapGatewayOperationsEndpoints();
        app.UseGatewayRateLimiting();
        app.UseGatewayQuotas();
        app.UseModelRouter();
        app.MapPrometheusScrapingEndpoint("/metrics");

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        return app;
    }

    /// <summary>
    /// Cache policy for <c>/admin</c> static assets.
    /// </summary>
    /// <remarks>
    /// One policy for the whole surface used to mean <c>no-store</c> on everything, so every visit
    /// re-fetched ~925 KB — including 273 KB of fonts that have never changed — with not one cache
    /// hit. The split is by whether the asset's <em>URL path</em> identifies its content, which is
    /// the only thing that makes a year-long <c>immutable</c> safe. See
    /// <see cref="IsImmutablyAddressed"/>; everything else stays <c>no-store</c>.
    /// </remarks>
    private static void ApplyAdminCachePolicy(StaticFileResponseContext ctx)
    {
        var headers = ctx.Context.Response.Headers;

        if (IsImmutablyAddressed(ctx.Context.Request.Path))
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
            // The old policy set Pragma on every admin response; leaving it on an immutable asset
            // would contradict Cache-Control for HTTP/1.0 intermediaries.
            headers.Remove("Pragma");
            return;
        }

        headers.CacheControl = "no-store, no-cache, must-revalidate";
        headers.Pragma = "no-cache";
    }

    /// <summary>
    /// True when the request path alone identifies the bytes, so the same URL can never come to
    /// mean something else and a year is safe.
    /// </summary>
    /// <remarks>
    /// Two forms qualify today:
    /// <list type="bullet">
    /// <item><description>A binary font face under <c>/admin/vendor/fonts/</c>. These are vendored
    /// artefacts, not source: a different face is a different file. (If one is ever re-subsetted,
    /// add it under a new name rather than editing it in place.)</description></item>
    /// <item><description>A version in the file name — <c>alpine-csp-3.14.9.min.js</c> — so an
    /// upgrade is necessarily a new URL. Content-hashed names (<c>app-a1b2c3d4.js</c>) join this
    /// clause once the frontend build lands.</description></item>
    /// </list>
    /// Directory is deliberately <em>not</em> the test. <c>vendor/fonts.css</c> lives beside the
    /// faces but is hand-maintained source versioned only by <c>?v=1</c>, exactly like
    /// <c>admin.css?v=24</c>; a query string is not part of the cache identity for every
    /// intermediary, so anything versioned that way must stay <c>no-store</c> or an edit strands
    /// operators on a stale console for a year.
    /// </remarks>
    private static bool IsImmutablyAddressed(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (path.StartsWithSegments("/admin/vendor/fonts", StringComparison.OrdinalIgnoreCase)
            && value.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasVersionInFileName(value.AsSpan(value.LastIndexOf('/') + 1));
    }

    /// <summary>
    /// Looks for a numeric version segment in a file name: a run of digits introduced by <c>-</c> or
    /// <c>.</c> and closed by <c>.</c>, which matches the <c>3</c>, <c>14</c> and <c>9</c> of
    /// <c>alpine-csp-3.14.9.min.js</c> while rejecting <c>admin-app.js</c> and <c>fonts.css</c>.
    /// </summary>
    private static bool HasVersionInFileName(ReadOnlySpan<char> fileName)
    {
        for (var i = 1; i < fileName.Length; i++)
        {
            if (fileName[i - 1] is not ('-' or '.'))
            {
                continue;
            }

            var end = i;
            while (end < fileName.Length && char.IsAsciiDigit(fileName[end]))
            {
                end++;
            }

            if (end > i && end < fileName.Length && fileName[end] == '.')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Honours <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> from the proxies the operator has
    /// declared trustworthy, so the address the gateway partitions anonymous limits by is the
    /// caller's rather than the ingress's.
    /// </summary>
    private static void UseGatewayForwardedHeaders(this WebApplication app)
    {
        var options = app.Services
            .GetRequiredService<IOptions<GatewayOptions>>()
            .Value
            .ForwardedHeaders;

        if (!options.Enabled)
        {
            return;
        }

        const ForwardedHeaders headers = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = headers,
            ForwardLimit = options.ForwardLimit,
        };

        if (options.TrustAllProxies)
        {
            // Both collections default to loopback, and the middleware treats a non-empty collection
            // as an allow-list. Emptying them is what makes it accept the header from any peer.
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            app.Logger.LogWarning(
                "Forwarded headers are trusted from ANY peer (Gateway:ForwardedHeaders:TrustAllProxies). "
                + "Anything that can reach this port can choose the address its anonymous rate limits and "
                + "quotas are counted against. Restrict the port to your proxy, or name the proxy in "
                + "Gateway:ForwardedHeaders:KnownProxies/KnownNetworks instead.");
        }
        else
        {
            foreach (var proxy in options.GetKnownProxies())
            {
                forwarded.KnownProxies.Add(proxy);
            }

            foreach (var network in options.GetKnownNetworks())
            {
                forwarded.KnownIPNetworks.Add(network);
            }

            if (options.HasNoExplicitTrustAnchors)
            {
                app.Logger.LogWarning(
                    "Gateway:ForwardedHeaders:Enabled is true but no KnownProxies or KnownNetworks are "
                    + "configured, so only a proxy on loopback is trusted. A proxy on any other host — an "
                    + "ingress or a sidecar — will have its headers ignored and every anonymous caller will "
                    + "still share one rate-limit partition.");
            }
        }

        app.UseForwardedHeaders(forwarded);
    }
}
