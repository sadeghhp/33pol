using System.Net;
using System.Text;
using FluentAssertions;
using Pol33.Api.Services;

namespace Pol33.Api.Tests.Services;

public sealed class OpenAiCompatibleProviderModelsClientTests
{
    [Fact]
    public async Task ListModelsAsync_Upstream401_ThrowsProviderModelsUpstreamException()
    {
        var client = new OpenAiCompatibleProviderModelsClient(
            new HttpClient(new StatusCodeHandler(HttpStatusCode.Unauthorized)));

        var act = () => client.ListModelsAsync(
            new Uri("https://api.example.com/v1/models"),
            "token",
            CancellationToken.None);

        await act.Should().ThrowAsync<ProviderModelsUpstreamException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.Unauthorized);
    }

    private sealed class StatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }
}
