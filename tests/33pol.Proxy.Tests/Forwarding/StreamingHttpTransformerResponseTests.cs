using System.Net;
using Microsoft.AspNetCore.Http;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class StreamingHttpTransformerResponseTests
{
    [Fact]
    public async Task TransformResponseAsync_Streaming_SetsSseHeaders()
    {
        var transformer = new StreamingHttpTransformer(isStreaming: true, "alias", "canonical");
        var context = new DefaultHttpContext();
        context.Response.Headers.ContentLength = 100;

        await transformer.TransformResponseAsync(
            context,
            new HttpResponseMessage(HttpStatusCode.OK),
            CancellationToken.None);

        context.Response.Headers.ContainsKey("Content-Length").Should().BeFalse();
        context.Response.Headers.CacheControl.ToString().Should().Be("no-cache");
        context.Response.Headers["X-Accel-Buffering"].ToString().Should().Be("no");
    }
}
