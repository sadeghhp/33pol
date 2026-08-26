using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Models;
using Pol33.Core.RateLimiting;
using Pol33.Core.Security;
using Pol33.Policy.RateLimiting;
using Pol33.Proxy.Errors;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Tests.Middleware;

/// <summary>
/// The <c>model</c> scope's bucket is shared by every caller of that model, and grants are enforced
/// one middleware later. Charging the bucket before the grant is checked turned "a key asks for a
/// model it was never granted" into a cross-tenant denial of service: the ungranted caller gets its
/// 403 either way, but every tenant that <em>is</em> granted the model sees 429s.
/// </summary>
public sealed class RateLimitMiddlewareGrantScopeTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ApiKeyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task InvokeAsync_AnUngrantedCaller_DoesNotSpendTheSharedModelBudget()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var modelTier = new RateLimitPolicy(10, 0, 0);
        var middleware = Create(store, modelTier, granted: false);

        for (var i = 0; i < 50; i++)
        {
            (await Invoke(middleware)).StatusCode.Should().Be(
                StatusCodes.Status200OK,
                "the 403 is the router's to give; this middleware only declines to charge");
        }

        store.PeekRequest(RateLimitKeys.Model("gpt-4"), modelTier, TimeProvider.System.GetUtcNow())
            .Remaining.Should().Be(10, "not one of those requests may touch a bucket shared with every other tenant");
    }

    /// <summary>
    /// Not charging the model scope must not make the attempts free: the caller's own identity
    /// scopes are still debited, so a key spraying ungranted models is bounded by its tenant tier.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AnUngrantedCaller_IsStillChargedItsOwnTenantBudget()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = Create(store, new RateLimitPolicy(1000, 0, 0), granted: false, tenantRpm: 3);

        for (var i = 0; i < 3; i++)
        {
            (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task InvokeAsync_AGrantedCaller_IsChargedTheModelBudgetAsBefore()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = Create(store, new RateLimitPolicy(1, 0, 0), granted: true);

        (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status200OK);

        var refused = await Invoke(middleware);
        refused.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Headers[GatewayHeaders.RateLimitScope].ToString().Should().Be("model");
    }

    /// <summary>
    /// A public model has no grants to check, so the shared bucket is charged for everyone — which
    /// is the whole point of a per-model limit on a model anyone may call.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_APublicModel_IsChargedWithoutAGrantCheck()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var grants = Substitute.For<IModelGrantService>();
        grants.IsModelAllowedAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var middleware = Create(store, new RateLimitPolicy(1, 0, 0), grants: grants, publicAccess: true);

        (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status200OK);
        (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);

        await grants.DidNotReceive().IsModelAllowedAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The same reasoning covers a request carrying no usable identity: the router refuses it, so
    /// charging the shared bucket would hand the identical denial of service to anonymous traffic.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_AnUnauthenticatedRequest_DoesNotSpendTheSharedModelBudget()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var modelTier = new RateLimitPolicy(5, 0, 0);
        var middleware = Create(store, modelTier, granted: true);

        for (var i = 0; i < 20; i++)
        {
            await Invoke(middleware, authenticated: false);
        }

        store.PeekRequest(RateLimitKeys.Model("gpt-4"), modelTier, TimeProvider.System.GetUtcNow())
            .Remaining.Should().Be(5);
    }

    /// <summary>
    /// With authentication switched off there are no grants to consult, so the model scope applies
    /// to every caller exactly as it did before this check existed.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenAuthenticationIsNotRequired_ChargesTheModelBudget()
    {
        var store = new InMemoryDistributedRateLimitStore();
        var middleware = Create(store, new RateLimitPolicy(1, 0, 0), granted: false, authRequired: false);

        (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status200OK);
        (await Invoke(middleware)).StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    private static RateLimitMiddleware Create(
        IDistributedRateLimitStore store,
        RateLimitPolicy modelTier,
        bool granted = true,
        int tenantRpm = 100_000,
        bool authRequired = true,
        bool publicAccess = false,
        IModelGrantService? grants = null)
    {
        grants ??= CreateGrants(granted);

        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel(Arg.Any<string>(), out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig
                {
                    Id = "gpt-4",
                    Url = "http://backend:8000",
                    PublicAccess = publicAccess,
                };
                return true;
            });

        var authState = Substitute.For<IGatewayAuthenticationState>();
        authState.IsAuthenticationRequired.Returns(authRequired);

        var rateLimits = new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(tenantRpm, 0, 0),
            Models = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4"] = modelTier,
            },
        };

        return new RateLimitMiddleware(
            _ => Task.CompletedTask,
            new RateLimitPlanResolver(new StubConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits })),
            store,
            new OpenAiErrorResponseWriter(),
            Substitute.For<IGatewayMetricsCollector>(),
            registry,
            timeProvider: TimeProvider.System,
            modelGrants: grants,
            authState: authState);

        static IModelGrantService CreateGrants(bool granted)
        {
            var service = Substitute.For<IModelGrantService>();
            service.IsModelAllowedAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(granted);
            return service;
        }
    }

    private static async Task<HttpResponse> Invoke(RateLimitMiddleware middleware, bool authenticated = true)
    {
        var context = CreateContext(authenticated);
        await middleware.InvokeAsync(context);
        return context.Response;
    }

    private static DefaultHttpContext CreateContext(bool authenticated)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"model":"gpt-4"}"""));
        context.Response.Body = new MemoryStream();

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(GatewayAuthClaims.TenantId, TenantId.ToString()),
                    new Claim(GatewayAuthClaims.ApiKeyId, ApiKeyId.ToString()),
                ],
                authenticationType: "Test"));
        }

        return context;
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }
}
