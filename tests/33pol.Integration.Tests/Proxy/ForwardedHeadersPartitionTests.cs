using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Proxy;

/// <summary>
/// Anonymous traffic to a <c>publicAccess</c> model is rate limited per client address. Behind a
/// proxy that address is the proxy's own unless the gateway is told to read <c>X-Forwarded-For</c>,
/// which is what collapsed every public-model caller into one shared bucket.
/// </summary>
public sealed class ForwardedHeadersPartitionTests
{
    private const string ModelId = "local-mock";

    /// <summary>
    /// Two callers arriving through the same trusted proxy get their own rate-limit partitions
    /// rather than sharing one. The limit here is a single request per minute, so a shared partition
    /// is unmistakable: the second caller would be refused without having sent anything before.
    /// </summary>
    [Fact]
    public async Task TrustedProxy_PartitionsAnonymousCallersByForwardedAddress()
    {
        await using var factory = CreateFactory(trustForwardedHeaders: true);

        var first = await PostChatAsync(factory, forwardedFor: "203.0.113.10");
        var second = await PostChatAsync(factory, forwardedFor: "203.0.113.11");

        first.Should().Be(HttpStatusCode.OK);
        second.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The same caller through a trusted proxy is still held to the limit — partitioning by
    /// forwarded address must not become a way to opt out of rate limiting altogether.
    /// </summary>
    [Fact]
    public async Task TrustedProxy_StillLimitsRepeatCallsFromOneAddress()
    {
        await using var factory = CreateFactory(trustForwardedHeaders: true);

        var first = await PostChatAsync(factory, forwardedFor: "203.0.113.10");
        var second = await PostChatAsync(factory, forwardedFor: "203.0.113.10");

        first.Should().Be(HttpStatusCode.OK);
        second.Should().Be(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// With forwarded headers untrusted — the default — a client-supplied <c>X-Forwarded-For</c> is
    /// ignored.
    /// </summary>
    /// <remarks>
    /// The header is written by whoever sent the request, so honouring it from an untrusted peer
    /// would be worse than ignoring it: a caller could put a fresh fake address on every request and
    /// mint unlimited partitions, turning anonymous rate limiting off entirely.
    /// </remarks>
    [Fact]
    public async Task UntrustedProxy_IgnoresSpoofedForwardedAddress()
    {
        await using var factory = CreateFactory(trustForwardedHeaders: false);

        var first = await PostChatAsync(factory, forwardedFor: "203.0.113.10");
        var second = await PostChatAsync(factory, forwardedFor: "203.0.113.11");

        first.Should().Be(HttpStatusCode.OK);
        second.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private static async Task<HttpStatusCode> PostChatAsync(
        WebApplicationFactory<Program> factory,
        string forwardedFor)
    {
        var client = factory.CreateClient();
        using var content = new StringContent(
            $$"""{"model":"{{ModelId}}","stream":false}""",
            Encoding.UTF8,
            "application/json");
        content.Headers.ContentType!.CharSet = null;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = content,
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static WebApplicationFactory<Program> CreateFactory(bool trustForwardedHeaders)
    {
        var configPath = WritePublicModelConfig();
        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            upstreamHandler: new MockUpstreamHandler(),
            configureSettings: settings =>
            {
                IntegrationModelsConfig.ApplyStandardModelsSettings(settings, configPath);

                // One request per partition per minute, so a shared partition shows up immediately.
                settings["RateLimiting:Enabled"] = "true";
                settings["RateLimiting:Default:Rpm"] = "1";
                settings["RateLimiting:Default:Burst"] = "0";

                if (trustForwardedHeaders)
                {
                    settings["Gateway:ForwardedHeaders:Enabled"] = "true";
                    // The test server presents no remote address, so it cannot match a KnownProxies
                    // entry; this is the setting that trusts the header from any peer.
                    settings["Gateway:ForwardedHeaders:TrustAllProxies"] = "true";
                }
            });
    }

    private static string WritePublicModelConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"33pol-forwarded-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "models.json");
        const string json = """
            {
              "models": [
                {
                  "id": "local-mock",
                  "url": "http://127.0.0.1:18080",
                  "maxContextLength": 8192,
                  "aliases": [],
                  "publicAccess": true
                }
              ]
            }
            """;
        File.WriteAllText(path, json);
        return path;
    }
}
