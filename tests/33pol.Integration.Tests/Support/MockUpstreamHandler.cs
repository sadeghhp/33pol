using System.Net;
using System.Text;

namespace Pol33.Integration.Tests.Support;

internal sealed class MockUpstreamHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    public int SendCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        SendCount++;
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (IsStreamingRequest(LastRequestBody))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"id":"chunk-1","choices":[]}

                    data: {"usage":{"prompt_tokens":8,"completion_tokens":2}}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "id": "upstream-1",
                  "object": "chat.completion",
                  "model": "local-mock",
                  "usage": { "prompt_tokens": 12, "completion_tokens": 6 }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };
    }

    private static bool IsStreamingRequest(string? body) =>
        body is not null &&
        (body.Contains("\"stream\":true", StringComparison.Ordinal) ||
         body.Contains("\"stream\": true", StringComparison.Ordinal));
}
