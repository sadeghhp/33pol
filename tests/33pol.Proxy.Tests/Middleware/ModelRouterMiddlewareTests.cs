using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Configuration;
using Pol33.Registry.Health;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
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
        body.Should().Contain("model");
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

            var forwarder = Substitute.For<IHttpForwarder>();
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
                Arg.Any<HttpMessageInvoker>(),
                Arg.Any<ForwarderRequestConfig>(),
                Arg.Any<HttpTransformer>());
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
    }

    private static ModelRouterMiddleware CreateMiddleware(
        RequestDelegate? next = null,
        IModelRegistry? registry = null,
        IBackendHealthStore? healthStore = null,
        IHttpForwarder? forwarder = null)
    {
        next ??= _ => Task.CompletedTask;
        registry ??= Substitute.For<IModelRegistry>();
        if (healthStore is null)
        {
            var health = Substitute.For<IBackendHealthStore>();
            health.IsBackendHealthy(Arg.Any<string>()).Returns(true);
            healthStore = health;
        }

        forwarder ??= Substitute.For<IHttpForwarder>();

        return new ModelRouterMiddleware(
            next,
            registry,
            healthStore,
            forwarder,
            new HttpMessageInvoker(new HttpClientHandler()),
            NullLogger<ModelRouterMiddleware>.Instance);
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

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
