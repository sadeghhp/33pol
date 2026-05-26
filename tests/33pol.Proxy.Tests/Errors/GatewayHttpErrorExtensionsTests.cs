using System.Text;
using Microsoft.AspNetCore.Http;
using Pol33.Core.Errors;
using Pol33.Proxy.Errors;

namespace Pol33.Proxy.Tests.Errors;

public sealed class GatewayHttpErrorExtensionsTests
{
    [Fact]
    public async Task WriteGatewayErrorAsync_SetsStatusHeadersAndBody()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var response = new OpenAiErrorResponseWriter().Write(GatewayErrorCode.RateLimitExceeded);

        await context.WriteGatewayErrorAsync(response, CancellationToken.None, retryAfterSeconds: 30);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.Headers[GatewayHeaders.ErrorCode].ToString().Should().Be("rate_limit_exceeded");
        context.Response.Headers[GatewayHeaders.RetryAfter].ToString().Should().Be("30");

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        body.Should().Contain("rate_limit_exceeded");
    }
}
