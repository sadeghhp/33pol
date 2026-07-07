using System.Net;
using Pol33.Core.Providers;

namespace Pol33.Core.Tests.Providers;

public sealed class SsrfGuardingHttpHandlerTests
{
    [Theory]
    [InlineData("http://10.0.0.1/v1/models")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost/v1/models")]
    public async Task SendAsync_InternalTarget_Throws(string url)
    {
        using var invoker = CreateInvoker(out var inner);

        var act = async () => await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.Called.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_PublicLiteralAddress_PassesThrough()
    {
        using var invoker = CreateInvoker(out var inner);

        // 8.8.8.8 is a public literal IP: no DNS lookup, not in the blocklist.
        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://8.8.8.8/v1/models"), CancellationToken.None);

        inner.Called.Should().BeTrue();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpMessageInvoker CreateInvoker(out RecordingHandler inner)
    {
        inner = new RecordingHandler();
        var guard = new SsrfGuardingHttpHandler { InnerHandler = inner };
        return new HttpMessageInvoker(guard);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public bool Called { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
