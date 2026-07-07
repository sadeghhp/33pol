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
    public async Task InvokeAsync_BulkheadSaturated_Returns502UpstreamError()
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

            context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
            var body = await ReadResponseBodyAsync(context);
            body.Should().Contain("upstream_error");
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

    [Fact]
    public async Task InvokeAsync_ForwardTimeout_RecordsRecentRequestWithErrorCode()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-mw-timeout-{Guid.NewGuid():N}.json");
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
                    Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, callInfo.Arg<CancellationToken>()).ConfigureAwait(false);
                    return ForwarderError.None;
                });

            RecentRequestEntry? recorded = null;
            var recentRequestStore = Substitute.For<IRecentRequestStore>();
            recentRequestStore.When(x => x.Record(Arg.Any<RecentRequestEntry>()))
                .Do(call => recorded = call.Arg<RecentRequestEntry>());

            var gatewayOptions = new GatewayOptions { Resilience = new GatewayResilienceOptions { ForwardTimeoutSeconds = 1 } };
            var middleware = CreateMiddleware(
                registry: registry,
                healthStore: health,
                forwarder: forwarder,
                recentRequestStore: recentRequestStore,
                gatewayOptions: gatewayOptions);
            var context = CreateContext(
                HttpMethods.Post,
                "/v1/chat/completions",
                """{"model":"m1","stream":false}""");

            await middleware.InvokeAsync(context);

            recorded.Should().NotBeNull();
            recorded!.ErrorCode.Should().Be("upstream_error");
            recorded.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
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
        IBudgetEnforcementService? budgetEnforcement = null)
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
        requestTracker ??= Substitute.For<IRequestTracker>();
        requestTracker.BeginInferenceRequest(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(_ => Substitute.For<IInferenceRequestScope>());
        recentRequestStore ??= Substitute.For<IRecentRequestStore>();
        var usageRecorder = Substitute.For<IUsageRecorder>();
        var metricsCollector = Substitute.For<IGatewayMetricsCollector>();

        var options = gatewayOptions ?? new GatewayOptions();
        var gatewayOptionsWrapper = Options.Create(options);
        var circuitBreakers = new ModelCircuitBreakerRegistry(gatewayOptionsWrapper, metricsCollector);
        bulkhead ??= new BulkheadRegistry(gatewayOptionsWrapper, metricsCollector);
        var rateLimitResolver = Substitute.For<IRateLimitPolicyResolver>();
        rateLimitResolver.Resolve(Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new RateLimitPolicy(10_000, 1_000, 1_000));
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
