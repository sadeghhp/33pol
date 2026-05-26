using Microsoft.AspNetCore.Http;
using Pol33.Api.Middleware;
using Pol33.Core.Errors;

namespace Pol33.Api.Tests.Middleware;

public sealed class RequestIdMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_GeneratesRequestId_OnSuccessPath()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        RequestDelegate next = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        var sut = new RequestIdMiddleware(next);
        await sut.InvokeAsync(context);

        context.Items[RequestIdKeys.HttpContextItemKey].Should().BeOfType<string>();
        var requestId = (string)context.Items[RequestIdKeys.HttpContextItemKey]!;
        requestId.Should().StartWith("req_");
        context.Response.Headers[GatewayHeaders.RequestId].ToString().Should().Be(requestId);
    }

    [Fact]
    public async Task InvokeAsync_PropagatesIncomingRequestId()
    {
        const string incoming = "req_clientprovided123";
        var context = new DefaultHttpContext();
        context.Request.Headers[GatewayHeaders.RequestId] = incoming;
        context.Response.Body = new MemoryStream();
        RequestDelegate next = _ => Task.CompletedTask;

        var sut = new RequestIdMiddleware(next);
        await sut.InvokeAsync(context);

        context.Items[RequestIdKeys.HttpContextItemKey].Should().Be(incoming);
        context.Response.Headers[GatewayHeaders.RequestId].ToString().Should().Be(incoming);
    }

    [Fact]
    public void ResolveRequestId_EmptyHeader_GeneratesNewId()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[GatewayHeaders.RequestId] = "   ";

        var requestId = RequestIdMiddleware.ResolveRequestId(context.Request);

        requestId.Should().StartWith("req_");
        requestId.Should().NotBe("   ");
    }

    [Fact]
    public async Task InvokeAsync_ErrorResponse_StillSetsRequestIdHeader()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        RequestDelegate next = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.CompletedTask;
        };

        var sut = new RequestIdMiddleware(next);
        await sut.InvokeAsync(context);

        context.Response.Headers[GatewayHeaders.RequestId].ToString().Should().StartWith("req_");
    }
}
