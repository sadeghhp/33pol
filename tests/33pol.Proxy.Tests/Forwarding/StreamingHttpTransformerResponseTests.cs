using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class StreamingHttpTransformerResponseTests
{
    [Fact]
    public async Task TransformResponseAsync_Streaming_DelegatesHeadersToForwarder()
    {
        var transformer = new StreamingHttpTransformer(isStreaming: true, "alias", "canonical");
        var context = new DefaultHttpContext();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: hello\n\n", Encoding.UTF8, "text/event-stream"),
        };

        var shouldCopy = await transformer.TransformResponseAsync(
            context,
            response,
            CancellationToken.None);

        shouldCopy.Should().BeTrue();
        context.Response.Headers.ContainsKey("Content-Type").Should().BeFalse();
        context.Response.Headers.ContainsKey("Content-Length").Should().BeFalse();
    }
}
