using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Proxy.Middleware;
using Pol33.Proxy.Resilience;

namespace Pol33.Proxy.Tests.Middleware;

public sealed class InferenceResilienceMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_DrainingInference_Returns503GatewayDraining()
    {
        var drain = new GatewayDrainState();
        drain.BeginDrain();
        var middleware = CreateMiddleware(drain);
        var context = CreateInferenceContext();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("gateway_draining");
    }

    [Fact]
    public async Task InvokeAsync_ContentLengthOverLimit_Returns400RequestTooLarge()
    {
        var middleware = CreateMiddleware();
        var context = CreateInferenceContext();
        context.Request.ContentLength = 100_000_000;

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("request_too_large");
    }

    [Fact]
    public async Task InvokeAsync_HealthLive_AllowsNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/health/live";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    private static InferenceResilienceMiddleware CreateMiddleware(
        IGatewayDrainState? drain = null,
        RequestDelegate? next = null)
    {
        next ??= _ => Task.CompletedTask;
        drain ??= new GatewayDrainState();
        var options = Options.Create(new GatewayOptions
        {
            Resilience = new GatewayResilienceOptions { MaxRequestBodyBytes = 1024 },
        });

        return new InferenceResilienceMiddleware(next, drain, new OpenAiErrorResponseWriter(), options);
    }

    private static DefaultHttpContext CreateInferenceContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        return context;
    }
}
