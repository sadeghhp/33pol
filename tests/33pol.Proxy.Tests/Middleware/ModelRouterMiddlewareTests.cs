using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Proxy.Resilience;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Security;
using System.Security.Claims;
using Pol33.Core.RateLimiting;
using Pol33.Policy.CircuitBreaker;
using Pol33.Registry.Health;
using Pol33.Proxy.Forwarding;
using Pol33.Proxy.Middleware;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Tests.Middleware;

public sealed class ModelRouterMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_PassthroughPath_CallsNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(HttpMethods.Get, "/health/live");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task InvokeAsync_NonPostRoutablePath_CallsNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(HttpMethods.Get, "/v1/chat/completions");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_MissingModel_Returns400()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext(
            HttpMethods.Post,
            "/v1/chat/completions",
            """{"stream":false}""");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("missing_model");
    }

    [Fact]
    public async Task InvokeAsync_UnknownModel_Returns404()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("missing", out Arg.Any<ModelConfig?>()).Returns(false);

        var middleware = CreateMiddleware(registry: registry);
        var context = CreateContext(
            HttpMethods.Post,
            "/v1/chat/completions",
            """{"model":"missing"}""");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task InvokeAsync_UnhealthyBackend_Returns502()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-mw-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, """
            { "models": [ { "id": "m1", "url": "http://backend:8000", "aliases": [] } ] }
            """);

        try
        {
            var registry = new Pol33.Registry.Services.ModelRegistryService(
                NullLogger<Pol33.Registry.Services.ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(configPath);

            var health = new BackendHealthStore(Options.Create(new GatewayOptions()));
            health.SetHealth(new BackendHealth(
                "m1",
                "http://backend:8000",
                IsHealthy: false,
                StatusCode: 503,
                Error: "down",
                LastCheckedUtc: DateTimeOffset.UtcNow));

            var forwarder = Substitute.For<IInferenceHttpForwarder>();
            var middleware = CreateMiddleware(registry: registry, healthStore: health, forwarder: forwarder);
            var context = CreateContext(
                HttpMethods.Post,
                "/v1/chat/completions",
                """{"model":"m1"}""");

            await middleware.InvokeAsync(context);

            context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
            await forwarder.DidNotReceive().SendAsync(
                Arg.Any<HttpContext>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<StreamingHttpTransformer>(),
                Arg.Any<bool>(),
                Arg.Any<InferenceForwardTimeouts>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task InvokeAsync_HealthyModel_ForwardsToBackendUrl()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-mw-fwd-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, """
            { "models": [ { "id": "m1", "url": "http://backend:8000", "aliases": ["alias-m"] } ] }
            """);

        try
        {
            var registry = new Pol33.Registry.Services.ModelRegistryService(
                NullLogger<Pol33.Registry.Services.ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(configPath);

            var health = Substitute.For<IBackendHealthStore>();
            health.IsBackendHealthy("m1").Returns(true);

            var forwarder = Substitute.For<IInferenceHttpForwarder>();
            forwarder.SendAsync(
                    Arg.Any<HttpContext>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<StreamingHttpTransformer>(),
                    Arg.Any<bool>(),
                    Arg.Any<InferenceForwardTimeouts>(),
                    Arg.Any<CancellationToken>())
                .Returns(ForwarderError.None);

            var middleware = CreateMiddleware(registry: registry, healthStore: health, forwarder: forwarder);
            var context = CreateContext(
                HttpMethods.Post,
                "/v1/chat/completions",
                """{"model":"alias-m","stream":false}""");

            await middleware.InvokeAsync(context);

            await forwarder.Received(1).SendAsync(
                Arg.Any<HttpContext>(),
                "http://backend:8000",
                Arg.Any<string?>(),
                Arg.Any<StreamingHttpTransformer>(),
                false,
                Arg.Any<InferenceForwardTimeouts>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task InvokeAsync_PostRerank_ForwardsToBackend()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-mw-rerank-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, """
            { "models": [ { "id": "reranker", "url": "http://backend:8000", "aliases": [] } ] }
            """);

        try
        {
            var registry = new Pol33.Registry.Services.ModelRegistryService(
                NullLogger<Pol33.Registry.Services.ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(configPath);

            var health = Substitute.For<IBackendHealthStore>();
            health.IsBackendHealthy("reranker").Returns(true);

            var forwarder = Substitute.For<IInferenceHttpForwarder>();
            forwarder.SendAsync(
                    Arg.Any<HttpContext>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<StreamingHttpTransformer>(),
                    Arg.Any<bool>(),
                    Arg.Any<InferenceForwardTimeouts>(),
                    Arg.Any<CancellationToken>())
                .Returns(ForwarderError.None);

            var middleware = CreateMiddleware(registry: registry, healthStore: health, forwarder: forwarder);
            var context = CreateContext(
                HttpMethods.Post,
                "/v1/rerank",
                """{"model":"reranker","query":"test","documents":["doc"]}""");

            await middleware.InvokeAsync(context);

            await forwarder.Received(1).SendAsync(
                Arg.Any<HttpContext>(),
                "http://backend:8000",
                Arg.Any<string?>(),
                Arg.Any<StreamingHttpTransformer>(),
                false,
                Arg.Any<InferenceForwardTimeouts>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task InvokeAsync_InvalidJson_Returns400()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext(
            HttpMethods.Post,
            "/v1/chat/completions",
            "{ not-json");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("invalid_json");
    }

    private static DefaultHttpContext CreateContext(string method, string path, string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes(body ?? string.Empty));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static void SetInferenceUser(DefaultHttpContext context, Guid tenantId, Guid apiKeyId)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(GatewayAuthClaims.TenantId, tenantId.ToString()),
            new Claim(GatewayAuthClaims.ApiKeyId, apiKeyId.ToString()),
            new Claim(GatewayAuthClaims.Role, ApiKeyRole.Inference.ToString()),
        ],
        GatewayAuthSchemes.ApiKey);
        context.User = new ClaimsPrincipal(identity);
        context.Items[TenantContextKeys.HttpContextItemKey] = new TenantContext
        {
            TenantId = tenantId.ToString(),
            ApiKeyId = apiKeyId.ToString(),
            Role = ApiKeyRole.Inference,
        };
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task InvokeAsync_PublicModel_SkipsGrantCheckEvenWhenDenied()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("m1", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "m1", Url = "http://backend:8000", PublicAccess = true };
                return true;
            });

        var tenantId = Guid.NewGuid();
        var modelGrants = Substitute.For<IModelGrantService>();
        modelGrants.IsModelAllowedAsync(tenantId, Arg.Any<Guid>(), "m1", Arg.Any<CancellationToken>())
            .Returns(false);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateAsyncScope().Returns(scope);
        scope.ServiceProvider.Returns(scopeProvider);
        scopeProvider.GetService(typeof(IModelGrantService)).Returns(modelGrants);

        var authState = Substitute.For<IGatewayAuthenticationState>();
        authState.IsAuthenticationRequired.Returns(true);

        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy("m1").Returns(true);

        var forwarder = Substitute.For<IInferenceHttpForwarder>();
        forwarder.SendAsync(
                Arg.Any<HttpContext>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<StreamingHttpTransformer>(),
                Arg.Any<bool>(),
                Arg.Any<InferenceForwardTimeouts>(),
                Arg.Any<CancellationToken>())
            .Returns(ForwarderError.None);

        var middleware = CreateMiddleware(
            registry: registry,
            scopeFactory: scopeFactory,
            authState: authState,
            healthStore: health,
            forwarder: forwarder);

        var context = CreateContext(
            HttpMethods.Post,
            "/v1/chat/completions",
            """{"model":"m1"}""");
        SetInferenceUser(context, tenantId, Guid.NewGuid());

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().NotBe(StatusCodes.Status403Forbidden);
        await modelGrants.DidNotReceive()
            .IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_AuthRequired_NoKey_Returns401InvalidApiKey()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("m1", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "m1", Url = "http://backend:8000" };
                return true;
            });

        var authState = Substitute.For<IGatewayAuthenticationState>();
        authState.IsAuthenticationRequired.Returns(true);

        var middleware = CreateMiddleware(registry: registry, authState: authState);
        var context = CreateContext(
            HttpMethods.Post,
            "/v1/chat/completions",
            """{"model":"m1"}""");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("invalid_api_key");
    }

    [Fact]
    public async Task InvokeAsync_GrantDenied_Returns403InsufficientScope()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("m1", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "m1", Url = "http://backend:8000" };
                return true;
            });

        var tenantId = Guid.NewGuid();
        var modelGrants = Substitute.For<IModelGrantService>();
        modelGrants.IsModelAllowedAsync(tenantId, Arg.Any<Guid>(), "m1", Arg.Any<CancellationToken>())
            .Returns(false);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateAsyncScope().Returns(scope);
        scope.ServiceProvider.Returns(scopeProvider);
        scopeProvider.GetService(typeof(IModelGrantService)).Returns(modelGrants);

        var authState = Substitute.For<IGatewayAuthenticationState>();
        authState.IsAuthenticationRequired.Returns(true);

        var middleware = CreateMiddleware(
            registry: registry,
            scopeFactory: scopeFactory,
            authState: authState);

        var context = CreateContext(
            HttpMethods.Post,
            "/v1/chat/completions",
            """{"model":"m1"}""");
        SetInferenceUser(context, tenantId, Guid.NewGuid());

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("insufficient_scope");
    }

    [Fact]
    public async Task InvokeAsync_BulkheadSaturated_Returns429ConcurrencyLimit()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("m1", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "m1", Url = "http://backend:8000" };
                return true;
            });

        var bulkheadOptions = Options.Create(new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { MaxConcurrentForwardsPerModel = 1 },
        });
        var bulkhead = new BulkheadRegistry(bulkheadOptions, Substitute.For<IGatewayMetricsCollector>());
        var held = await bulkhead.TryAcquireAsync("m1", CancellationToken.None);

        try
        {
            var middleware = CreateMiddleware(registry: registry, bulkhead: bulkhead);
            var context = CreateContext(
                HttpMethods.Post,
                "/v1/chat/completions",
                """{"model":"m1"}""");

            await middleware.InvokeAsync(context);

            // Gateway-side admission control, not a backend failure. Reporting it as 502
            // backend_error told OpenAI-compatible routers the model itself had failed, so they
            // marked it down and failed over instead of simply retrying.
            context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
            var body = await ReadResponseBodyAsync(context);
            body.Should().Contain("concurrency_limit_exceeded");
            context.Response.Headers.RetryAfter.ToString().Should().NotBeNullOrEmpty();
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_UpstreamAuthConfiguredButTokenMissing_Returns502()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("cloud-model", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig
                {
                    Id = "cloud-model",
                    Url = "http://backend:8000",
                    UpstreamAuth = new UpstreamAuthConfig { Type = "bearer", EnvVar = "OPENAI_API_KEY" },
                };
                return true;
            });

        var tokenResolver = Substitute.For<IUpstreamBearerTokenResolver>();
        tokenResolver.ResolveBearerToken(Arg.Any<UpstreamAuthConfig?>()).Returns((string?)null);

        var middleware = CreateMiddleware(registry: registry, upstreamTokenResolver: tokenResolver);
        var context = CreateContext(
            HttpMethods.Post,
            "/v1/chat/completions",
            """{"model":"cloud-model","stream":false}""");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        var body = await ReadResponseBodyAsync(context);
        body.Should().Contain("Upstream auth token not configured");
    }

    [Fact]
    public async Task InvokeAsync_RoutedInference_BeginsRequestTracking()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("local-mock", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "local-mock", Url = "http://backend:8000" };
                return true;
            });

        var requestTracker = Substitute.For<IRequestTracker>();
        requestTracker.BeginInferenceRequest(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(_ => Substitute.For<IInferenceRequestScope>());

        var middleware = CreateMiddleware(registry: registry, requestTracker: requestTracker);
        var context = CreateContext(
            HttpMethods.Post,
            "/v1/chat/completions",
            """{"model":"local-mock","stream":false}""");

        await middleware.InvokeAsync(context);

        requestTracker.Received(1).BeginInferenceRequest("local-mock", false);
    }

    [Fact]
    public async Task InvokeAsync_ForwardFailure_RecordsRecentRequestWithErrorCode()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-mw-err-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, """
            { "models": [ { "id": "m1", "url": "http://backend:8000", "aliases": [] } ] }
            """);

        try
        {
            var registry = new Pol33.Registry.Services.ModelRegistryService(
                NullLogger<Pol33.Registry.Services.ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(configPath);

            var health = Substitute.For<IBackendHealthStore>();
            health.IsBackendHealthy("m1").Returns(true);

            var forwarder = Substitute.For<IInferenceHttpForwarder>();
            forwarder.SendAsync(
                    Arg.Any<HttpContext>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<StreamingHttpTransformer>(),
                    Arg.Any<bool>(),
                    Arg.Any<InferenceForwardTimeouts>(),
                    Arg.Any<CancellationToken>())
                .Returns(ForwarderError.Request);

            RecentRequestEntry? recorded = null;
            var recentRequestStore = Substitute.For<IRecentRequestStore>();
            recentRequestStore.When(x => x.Record(Arg.Any<RecentRequestEntry>()))
                .Do(call => recorded = call.Arg<RecentRequestEntry>());

            var middleware = CreateMiddleware(
                registry: registry,
                healthStore: health,
                forwarder: forwarder,
                recentRequestStore: recentRequestStore);
            var context = CreateContext(
                HttpMethods.Post,
                "/v1/chat/completions",
                """{"model":"m1","stream":false}""");

            await middleware.InvokeAsync(context);

            recorded.Should().NotBeNull();
            recorded!.ModelId.Should().Be("m1");
            recorded.ErrorCode.Should().Be("upstream_error");
            recorded.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task InvokeAsync_ForwardSuccess_RecordsRecentRequestWithoutErrorCode()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-mw-ok-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, """
            { "models": [ { "id": "m1", "url": "http://backend:8000", "aliases": [] } ] }
            """);

        try
        {
            var registry = new Pol33.Registry.Services.ModelRegistryService(
                NullLogger<Pol33.Registry.Services.ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(configPath);

            var health = Substitute.For<IBackendHealthStore>();
            health.IsBackendHealthy("m1").Returns(true);

            var forwarder = Substitute.For<IInferenceHttpForwarder>();
            forwarder.SendAsync(
                    Arg.Any<HttpContext>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<StreamingHttpTransformer>(),
                    Arg.Any<bool>(),
                    Arg.Any<InferenceForwardTimeouts>(),
                    Arg.Any<CancellationToken>())
                .Returns(ForwarderError.None);

            RecentRequestEntry? recorded = null;
            var recentRequestStore = Substitute.For<IRecentRequestStore>();
            recentRequestStore.When(x => x.Record(Arg.Any<RecentRequestEntry>()))
                .Do(call => recorded = call.Arg<RecentRequestEntry>());

            var middleware = CreateMiddleware(
                registry: registry,
                healthStore: health,
                forwarder: forwarder,
                recentRequestStore: recentRequestStore);
            var context = CreateContext(
                HttpMethods.Post,
                "/v1/chat/completions",
                """{"model":"m1","stream":false}""");

            await middleware.InvokeAsync(context);

            recorded.Should().NotBeNull();
            recorded!.ErrorCode.Should().BeNull();
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task InvokeAsync_UnknownModel_DoesNotRecordRecentRequest()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("missing", out Arg.Any<ModelConfig?>()).Returns(false);

        var recentRequestStore = Substitute.For<IRecentRequestStore>();
        var middleware = CreateMiddleware(registry: registry, recentRequestStore: recentRequestStore);
        var context = CreateContext(
            HttpMethods.Post,
            "/v1/chat/completions",
            """{"model":"missing"}""");

        await middleware.InvokeAsync(context);

        recentRequestStore.DidNotReceive().Record(Arg.Any<RecentRequestEntry>());
    }

    /// <summary>
    /// A header timeout (no response at all) remains a backend-health signal: it records an
    /// upstream_error to the client and counts as a circuit-breaker failure.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_HeaderTimeout_RecordsRecentRequestWithErrorCode()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            RecentRequestEntry? recorded = null;
            var recentRequestStore = Substitute.For<IRecentRequestStore>();
            recentRequestStore.When(x => x.Record(Arg.Any<RecentRequestEntry>()))
                .Do(call => recorded = call.Arg<RecentRequestEntry>());

            var middleware = CreateMiddleware(
                registry: registry,
                forwarder: CreateForwarderReturning(ForwarderError.RequestTimedOut),
                recentRequestStore: recentRequestStore);

            var context = CreateContext(
                HttpMethods.Post,
                "/v1/chat/completions",
                """{"model":"m1","stream":false}""");

            await middleware.InvokeAsync(context);

            recorded.Should().NotBeNull();
            recorded!.ErrorCode.Should().Be("upstream_error");
            recorded.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        });
    }

    /// <summary>
    /// The header timeout is the only deadline the middleware treats as backend ill-health, so it
    /// must still trip the breaker at the configured threshold.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_HeaderTimeout_CountsAsCircuitBreakerFailure()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var breakers = CreateBreakerRegistry(failureThreshold: 2);
            var middleware = CreateMiddleware(
                registry: registry,
                forwarder: CreateForwarderReturning(ForwarderError.RequestTimedOut),
                circuitBreakers: breakers);

            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":true}"""));
            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":true}"""));

            breakers.GetBreaker("m1").State.Should().Be(CircuitState.Open);
        });
    }

    /// <summary>
    /// A long but healthy stream is the case the split timeouts exist for: the forwarder returns
    /// success after far longer than ForwardTimeoutSeconds and the request must be recorded as a
    /// success, with the breaker closed.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_StreamOutlastingForwardTimeout_IsRecordedAsSuccess()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var breakers = CreateBreakerRegistry(failureThreshold: 1);

            var forwarder = Substitute.For<IInferenceHttpForwarder>();
            forwarder.SendAsync(
                    Arg.Any<HttpContext>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<StreamingHttpTransformer>(),
                    Arg.Any<bool>(),
                    Arg.Any<InferenceForwardTimeouts>(),
                    Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    // Comfortably longer than the 1s header timeout configured below. Under the old
                    // single-deadline design this was cancelled and recorded as a backend failure.
                    await Task.Delay(TimeSpan.FromMilliseconds(1_500)).ConfigureAwait(false);
                    return ForwarderError.None;
                });

            RecentRequestEntry? recorded = null;
            var recentRequestStore = Substitute.For<IRecentRequestStore>();
            recentRequestStore.When(x => x.Record(Arg.Any<RecentRequestEntry>()))
                .Do(call => recorded = call.Arg<RecentRequestEntry>());

            var middleware = CreateMiddleware(
                registry: registry,
                forwarder: forwarder,
                recentRequestStore: recentRequestStore,
                gatewayOptions: new GatewayOptions
                {
                    Resilience = new GatewayResilienceOptions { ForwardTimeoutSeconds = 1 },
                },
                circuitBreakers: breakers);

            var context = CreateContext(
                HttpMethods.Post,
                "/v1/chat/completions",
                """{"model":"m1","stream":true}""");

            await middleware.InvokeAsync(context);

            recorded.Should().NotBeNull();
            recorded!.ErrorCode.Should().BeNull();
            recorded.StatusCode.Should().Be(StatusCodes.Status200OK);
            recorded.DurationMs.Should().BeGreaterThan(1_000);
            breakers.GetBreaker("m1").State.Should().Be(CircuitState.Closed);
        });
    }

    /// <summary>
    /// A mid-stream stall is inconclusive about backend health (the backend already answered), so it
    /// abandons the probe instead of counting a failure. With a threshold of 1, a counted failure
    /// would open the circuit immediately.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_StreamIdleTimeout_DoesNotCountAsCircuitBreakerFailure()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var breakers = CreateBreakerRegistry(failureThreshold: 1);

            RecentRequestEntry? recorded = null;
            var recentRequestStore = Substitute.For<IRecentRequestStore>();
            recentRequestStore.When(x => x.Record(Arg.Any<RecentRequestEntry>()))
                .Do(call => recorded = call.Arg<RecentRequestEntry>());

            var middleware = CreateMiddleware(
                registry: registry,
                forwarder: CreateForwarderReturning(ForwarderError.ResponseBodyDestination),
                recentRequestStore: recentRequestStore,
                circuitBreakers: breakers);

            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":true}"""));

            breakers.GetBreaker("m1").State.Should().Be(CircuitState.Closed);
            recorded.Should().NotBeNull();
        });
    }

    /// <summary>
    /// A proxied 5xx is a backend failure even though forwarding itself succeeded. Counting it as a
    /// breaker success meant a backend that degraded into fast 500s closed a half-open breaker on
    /// its first probe and could never re-open it.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Upstream5xx_CountsAsCircuitBreakerFailure()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var breakers = CreateBreakerRegistry(failureThreshold: 2);

            RecentRequestEntry? recorded = null;
            var recentRequestStore = Substitute.For<IRecentRequestStore>();
            recentRequestStore.When(x => x.Record(Arg.Any<RecentRequestEntry>()))
                .Do(call => recorded = call.Arg<RecentRequestEntry>());

            var middleware = CreateMiddleware(
                registry: registry,
                forwarder: CreateForwarderProxyingStatus(StatusCodes.Status500InternalServerError),
                recentRequestStore: recentRequestStore,
                circuitBreakers: breakers);

            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":false}"""));
            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":false}"""));

            breakers.GetBreaker("m1").State.Should().Be(CircuitState.Open);
            recorded.Should().NotBeNull();
        });
    }

    /// <summary>
    /// A proxied 4xx means the backend answered and the request was wrong — not a health signal.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Upstream4xx_DoesNotCountAsCircuitBreakerFailure()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var breakers = CreateBreakerRegistry(failureThreshold: 1);
            var middleware = CreateMiddleware(
                registry: registry,
                forwarder: CreateForwarderProxyingStatus(StatusCodes.Status400BadRequest),
                circuitBreakers: breakers);

            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":false}"""));

            breakers.GetBreaker("m1").State.Should().Be(CircuitState.Closed);
        });
    }

    /// <summary>
    /// The breaker and the dashboard ask different questions of the same 4xx. Conflating them meant
    /// a model rejecting every call with 400 reported "0 errors, 0.00% error rate" on the console
    /// beside a live feed full of red rows.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_Upstream4xx_IsCountedAsAnErrorOnTheDashboard()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var scope = Substitute.For<IInferenceRequestScope>();
            var tracker = CreateTrackerReturning(scope);
            var recent = Substitute.For<IRecentRequestStore>();

            var middleware = CreateMiddleware(
                registry: registry,
                forwarder: CreateForwarderProxyingStatus(StatusCodes.Status400BadRequest),
                requestTracker: tracker,
                recentRequestStore: recent);

            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":false}"""));

            scope.Received(1).SetOutcome(false, "upstream_4xx");
            recent.Received(1).Record(Arg.Is<RecentRequestEntry>(e => e.StatusCode == 400));
        });
    }

    [Fact]
    public async Task InvokeAsync_Upstream2xx_IsCountedAsSuccess()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var scope = Substitute.For<IInferenceRequestScope>();
            var middleware = CreateMiddleware(
                registry: registry,
                forwarder: CreateForwarderProxyingStatus(StatusCodes.Status200OK),
                requestTracker: CreateTrackerReturning(scope));

            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":false}"""));

            scope.Received(1).SetOutcome(true, null);
        });
    }

    /// <summary>
    /// Admission rejections answer the client with an error, so they have to reach the console's
    /// counters and live feed. Reporting them to Prometheus alone is what let a saturated gateway
    /// render as calm and error-free while it turned every request away.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_UnhealthyBackend_RecordsRejectionForTheDashboard()
    {
        var registry = CreateSingleModelRegistry();
        var health = Substitute.For<IBackendHealthStore>();
        health.IsBackendHealthy(Arg.Any<string>()).Returns(false);

        var tracker = CreateTrackerReturning(Substitute.For<IInferenceRequestScope>());
        var recent = Substitute.For<IRecentRequestStore>();
        var middleware = CreateMiddleware(
            registry: registry, healthStore: health, requestTracker: tracker, recentRequestStore: recent);

        await middleware.InvokeAsync(CreateContext(
            HttpMethods.Post, "/v1/chat/completions", """{"model":"m1"}"""));

        tracker.Received(1).RecordRejectedRequest("m1", "backend_unhealthy");
        recent.Received(1).Record(Arg.Is<RecentRequestEntry>(e =>
            e.ModelId == "m1" && e.StatusCode >= 400 && !e.IsInFlight));
    }

    [Fact]
    public async Task InvokeAsync_CircuitOpen_RecordsRejectionForTheDashboard()
    {
        var registry = CreateSingleModelRegistry();
        var breakers = CreateBreakerRegistry(failureThreshold: 1);

        // Trip it: one 5xx forward is enough at a threshold of one.
        var tripped = CreateMiddleware(
            registry: registry,
            forwarder: CreateForwarderProxyingStatus(StatusCodes.Status500InternalServerError),
            circuitBreakers: breakers);
        await tripped.InvokeAsync(CreateContext(
            HttpMethods.Post, "/v1/chat/completions", """{"model":"m1"}"""));
        breakers.GetBreaker("m1").State.Should().Be(CircuitState.Open);

        var tracker = CreateTrackerReturning(Substitute.For<IInferenceRequestScope>());
        var recent = Substitute.For<IRecentRequestStore>();
        var middleware = CreateMiddleware(
            registry: registry, circuitBreakers: breakers, requestTracker: tracker, recentRequestStore: recent);

        await middleware.InvokeAsync(CreateContext(
            HttpMethods.Post, "/v1/chat/completions", """{"model":"m1"}"""));

        tracker.Received(1).RecordRejectedRequest("m1", "circuit_open");
        recent.Received(1).Record(Arg.Any<RecentRequestEntry>());
    }

    [Fact]
    public async Task InvokeAsync_BulkheadSaturated_RecordsRejectionForTheDashboard()
    {
        var registry = CreateSingleModelRegistry();
        var bulkhead = new BulkheadRegistry(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions { MaxConcurrentForwardsPerModel = 1 },
            }),
            Substitute.For<IGatewayMetricsCollector>());
        var held = await bulkhead.TryAcquireAsync("m1", CancellationToken.None);

        try
        {
            var tracker = CreateTrackerReturning(Substitute.For<IInferenceRequestScope>());
            var recent = Substitute.For<IRecentRequestStore>();
            var middleware = CreateMiddleware(
                registry: registry, bulkhead: bulkhead, requestTracker: tracker, recentRequestStore: recent);

            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1"}"""));

            tracker.Received(1).RecordRejectedRequest("m1", "bulkhead_full");
            recent.Received(1).Record(Arg.Is<RecentRequestEntry>(e => e.StatusCode == 429));
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>
    /// The request has to be on the feed while it is running, not only once it has finished — that
    /// is the whole point of the in-flight entry.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhileForwarding_PublishesAnInFlightEntryThenRetiresIt()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var recent = Substitute.For<IRecentRequestStore>();
            RecentRequestEntry? published = null;
            var publishedDuringForward = false;
            recent.When(r => r.BeginInFlight(Arg.Any<RecentRequestEntry>()))
                .Do(call => published = call.Arg<RecentRequestEntry>());

            var forwarder = Substitute.For<IInferenceHttpForwarder>();
            forwarder.SendAsync(
                    Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<StreamingHttpTransformer>(), Arg.Any<bool>(),
                    Arg.Any<InferenceForwardTimeouts>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    // Observed from inside the forward: the entry must already be visible here.
                    publishedDuringForward = published is not null;
                    call.Arg<HttpContext>().Response.StatusCode = StatusCodes.Status200OK;
                    return ForwarderError.None;
                });

            var middleware = CreateMiddleware(
                registry: registry, forwarder: forwarder, recentRequestStore: recent);

            await middleware.InvokeAsync(CreateContext(
                HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":false}"""));

            publishedDuringForward.Should().BeTrue();
            published!.IsInFlight.Should().BeTrue();
            published.ModelId.Should().Be("m1");
            published.StatusCode.Should().Be(0);

            recent.Received(1).Record(Arg.Is<RecentRequestEntry>(e => !e.IsInFlight));
            recent.Received(1).CompleteInFlight(published.RequestId);
        });
    }

    private static IRequestTracker CreateTrackerReturning(IInferenceRequestScope scope)
    {
        var tracker = Substitute.For<IRequestTracker>();
        tracker.BeginInferenceRequest(Arg.Any<string>(), Arg.Any<bool>()).Returns(scope);
        return tracker;
    }

    private static IModelRegistry CreateSingleModelRegistry()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("m1", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "m1", Url = "http://backend:8000" };
                return true;
            });
        return registry;
    }

    /// <summary>
    /// A burst of client disconnects must not be able to take a healthy model offline.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ClientCancellation_DoesNotCountAsCircuitBreakerFailure()
    {
        await WithSingleModelRegistryAsync(async registry =>
        {
            var breakers = CreateBreakerRegistry(failureThreshold: 1);
            var middleware = CreateMiddleware(
                registry: registry,
                forwarder: CreateForwarderReturning(ForwarderError.RequestCanceled),
                circuitBreakers: breakers);

            for (var i = 0; i < 3; i++)
            {
                await middleware.InvokeAsync(CreateContext(
                    HttpMethods.Post, "/v1/chat/completions", """{"model":"m1","stream":true}"""));
            }

            breakers.GetBreaker("m1").State.Should().Be(CircuitState.Closed);
        });
    }

    private static IInferenceHttpForwarder CreateForwarderReturning(ForwarderError error)
    {
        var forwarder = Substitute.For<IInferenceHttpForwarder>();
        forwarder.SendAsync(
                Arg.Any<HttpContext>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<StreamingHttpTransformer>(),
                Arg.Any<bool>(),
                Arg.Any<InferenceForwardTimeouts>(),
                Arg.Any<CancellationToken>())
            .Returns(error);
        return forwarder;
    }

    /// <summary>
    /// Mimics the real forwarder proxying an upstream response: the status code is written to the
    /// response and the return value is None, because forwarding itself did not fail.
    /// </summary>
    private static IInferenceHttpForwarder CreateForwarderProxyingStatus(int statusCode)
    {
        var forwarder = Substitute.For<IInferenceHttpForwarder>();
        forwarder.SendAsync(
                Arg.Any<HttpContext>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<StreamingHttpTransformer>(),
                Arg.Any<bool>(),
                Arg.Any<InferenceForwardTimeouts>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<HttpContext>().Response.StatusCode = statusCode;
                return ForwarderError.None;
            });
        return forwarder;
    }

    private static ModelCircuitBreakerRegistry CreateBreakerRegistry(int failureThreshold) =>
        new(
            Options.Create(new GatewayOptions
            {
                Resilience = new GatewayResilienceOptions
                {
                    CircuitBreakerFailureThreshold = failureThreshold,
                    CircuitBreakerBreakDurationSeconds = 60,
                },
            }),
            Substitute.For<IGatewayMetricsCollector>());

    private static async Task WithSingleModelRegistryAsync(
        Func<Pol33.Registry.Services.ModelRegistryService, Task> body)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-mw-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, """
            { "models": [ { "id": "m1", "url": "http://backend:8000", "aliases": [] } ] }
            """);

        try
        {
            var registry = new Pol33.Registry.Services.ModelRegistryService(
                NullLogger<Pol33.Registry.Services.ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(configPath);
            await body(registry);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private static ModelRouterMiddleware CreateMiddleware(
        RequestDelegate? next = null,
        IModelRegistry? registry = null,
        IBackendHealthStore? healthStore = null,
        IInferenceHttpForwarder? forwarder = null,
        IErrorResponseWriter? errorWriter = null,
        IRequestTracker? requestTracker = null,
        IRecentRequestStore? recentRequestStore = null,
        IServiceScopeFactory? scopeFactory = null,
        IGatewayAuthenticationState? authState = null,
        BulkheadRegistry? bulkhead = null,
        IUpstreamBearerTokenResolver? upstreamTokenResolver = null,
        GatewayOptions? gatewayOptions = null,
        IBudgetEnforcementService? budgetEnforcement = null,
        ModelCircuitBreakerRegistry? circuitBreakers = null)
    {
        next ??= _ => Task.CompletedTask;
        registry ??= Substitute.For<IModelRegistry>();
        if (healthStore is null)
        {
            var health = Substitute.For<IBackendHealthStore>();
            health.IsBackendHealthy(Arg.Any<string>()).Returns(true);
            healthStore = health;
        }

        forwarder ??= Substitute.For<IInferenceHttpForwarder>();

        var modelGrants = Substitute.For<IModelGrantService>();
        modelGrants.IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        scopeFactory ??= CreateGrantScopeFactory(modelGrants);
        authState ??= CreateOpenAuthState();

        errorWriter ??= new OpenAiErrorResponseWriter();
        if (requestTracker is null)
        {
            // Only the default gets the throwaway-scope stub: re-stubbing a caller-supplied tracker
            // would silently discard the scope a test set up to assert its outcome on.
            var defaultTracker = Substitute.For<IRequestTracker>();
            defaultTracker.BeginInferenceRequest(Arg.Any<string>(), Arg.Any<bool>())
                .Returns(_ => Substitute.For<IInferenceRequestScope>());
            requestTracker = defaultTracker;
        }

        recentRequestStore ??= Substitute.For<IRecentRequestStore>();
        var usageRecorder = Substitute.For<IUsageRecorder>();
        var metricsCollector = Substitute.For<IGatewayMetricsCollector>();

        var options = gatewayOptions ?? new GatewayOptions();
        var gatewayOptionsWrapper = Options.Create(options);
        circuitBreakers ??= new ModelCircuitBreakerRegistry(gatewayOptionsWrapper, metricsCollector);
        bulkhead ??= new BulkheadRegistry(gatewayOptionsWrapper, metricsCollector);
        var rateLimitResolver = Substitute.For<IRateLimitPolicyResolver>();
        rateLimitResolver.Resolve(Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new RateLimitPolicy(10_000, 1_000, 1_000));
        // Must be stubbed explicitly: an unconfigured substitute returns false, which would silently
        // bypass the stream-slot path these tests exercise.
        rateLimitResolver.IsEnabled().Returns(true);
        var rateLimitStore = Substitute.For<IDistributedRateLimitStore>();
        rateLimitStore.TryAcquireStreamSlot(Arg.Any<string>(), Arg.Any<RateLimitPolicy>())
            .Returns(new RateLimitAcquireResult(true));

        return new ModelRouterMiddleware(
            next,
            registry,
            healthStore,
            scopeFactory,
            authState,
            errorWriter,
            requestTracker,
            recentRequestStore,
            usageRecorder,
            metricsCollector,
            circuitBreakers,
            bulkhead,
            rateLimitResolver,
            rateLimitStore,
            forwarder,
            gatewayOptionsWrapper,
            upstreamTokenResolver ?? Substitute.For<IUpstreamBearerTokenResolver>(),
            budgetEnforcement ?? CreateAllowAllBudgetEnforcement(),
            NullLogger<ModelRouterMiddleware>.Instance);
    }

    private static IBudgetEnforcementService CreateAllowAllBudgetEnforcement()
    {
        var enforcement = Substitute.For<IBudgetEnforcementService>();
        enforcement.CheckBeforeForwardAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.Allowed);
        enforcement.TryReserveAsync(
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCheckResult.Allowed);
        return enforcement;
    }

    private static IServiceScopeFactory CreateGrantScopeFactory(IModelGrantService modelGrants)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var scopeProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateAsyncScope().Returns(scope);
        scope.ServiceProvider.Returns(scopeProvider);
        scopeProvider.GetService(typeof(IModelGrantService)).Returns(modelGrants);
        return scopeFactory;
    }

    private static IGatewayAuthenticationState CreateOpenAuthState()
    {
        var authState = Substitute.For<IGatewayAuthenticationState>();
        authState.IsAuthenticationRequired.Returns(false);
        return authState;
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
