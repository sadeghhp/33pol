using System.Net;
using System.Text;
using Pol33.Api.Contracts;
using Pol33.Api.Services;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Tests.Services;

public sealed class AdminModelTestServiceTests
{
    [Theory]
    [InlineData("http://localhost:8080", "http://localhost:8080/v1/chat/completions")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api", "https://openrouter.ai/api/v1/chat/completions")]
    public void BuildChatCompletionsUri_NormalizesBase(string baseUrl, string expected)
    {
        AdminModelTestService.BuildChatCompletionsUri(baseUrl).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(99, 16)]
    public void ClampMaxTokens_EnforcesBounds(int? input, int expected)
    {
        AdminModelTestService.ClampMaxTokens(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizePrompt_UsesDefaultAndTrims()
    {
        AdminModelTestService.NormalizePrompt(null).Should().Be(AdminModelTestService.DefaultPrompt);
        AdminModelTestService.NormalizePrompt("  hi  ").Should().Be("hi");
    }

    [Fact]
    public void TryExtractAssistantContent_ParsesFirstChoice()
    {
        const string body =
            """
            {
              "choices": [
                { "message": { "content": "pong" } }
              ]
            }
            """;

        AdminModelTestService.TryExtractAssistantContent(body).Should().Be("pong");
    }

    [Fact]
    public async Task TestAsync_Success_ReturnsContentAndLatency()
    {
        var handler = new StubChatHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"hello upstream"}}]}""");
        var service = CreateService(
            handler,
            CreateModel("demo", "http://upstream.test", secretRef: "file:model:demo"));

        var result = await service.TestAsync("demo", new() { Prompt = "ping", MaxTokens = 2 });

        result.Ok.Should().BeTrue();
        result.ModelId.Should().Be("demo");
        result.StatusCode.Should().Be(200);
        result.Content.Should().Be("hello upstream");
        result.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
        handler.LastAuthorization.Should().Be("Bearer test-secret");
    }

    [Fact]
    public async Task TestAsync_UnknownModel_Returns404()
    {
        var service = CreateService(new StubChatHandler(HttpStatusCode.OK, "{}"), []);

        var result = await service.TestAsync("missing", null);

        result.Ok.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(404);
        result.Detail.Should().Contain("missing");
    }

    [Fact]
    public async Task TestAsync_MissingBearerWhenRequired_Returns400()
    {
        var service = CreateService(
            new StubChatHandler(HttpStatusCode.OK, "{}"),
            CreateModel("secured", "http://upstream.test", secretRef: "file:model:missing"));

        var result = await service.TestAsync("secured", null);

        result.Ok.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(400);
        result.Detail.Should().Contain("no API key");
    }

    [Fact]
    public async Task TestAsync_UpstreamError_ReturnsOkFalseWithDetail()
    {
        var handler = new StubChatHandler(
            HttpStatusCode.Unauthorized,
            """{"error":{"message":"invalid key"}}""");
        var service = CreateService(handler, CreateModel("demo", "http://upstream.test"));

        var result = await service.TestAsync("demo", null);

        result.Ok.Should().BeFalse();
        result.StatusCode.Should().Be(401);
        result.Detail.Should().Contain("invalid key");
        result.SuggestedStatusCode.Should().Be(200);
    }

    private static AdminModelTestService CreateService(
        HttpMessageHandler handler,
        params ModelConfig[] models) =>
        CreateService(handler, (IReadOnlyList<ModelConfig>)models);

    private static AdminModelTestService CreateService(HttpMessageHandler handler, IReadOnlyList<ModelConfig> models)
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel(Arg.Any<string>(), out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                var id = call.ArgAt<string>(0);
                var match = models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    call[1] = null;
                    return false;
                }

                call[1] = match;
                return true;
            });

        var resolver = Substitute.For<IUpstreamBearerTokenResolver>();
        resolver.ResolveBearerToken(Arg.Any<UpstreamAuthConfig?>())
            .Returns(call =>
            {
                var auth = call.ArgAt<UpstreamAuthConfig?>(0);
                if (auth?.SecretRef is null)
                {
                    return null;
                }

                return auth.SecretRef.Contains("missing", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "test-secret";
            });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AdminModelTestService.HttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new AdminModelTestService(registry, resolver, factory);
    }

    private static ModelConfig CreateModel(string id, string url, string? secretRef = null)
    {
        UpstreamAuthConfig? auth = secretRef is null
            ? null
            : new UpstreamAuthConfig { Type = "bearer", SecretRef = secretRef };

        return new ModelConfig { Id = id, Url = url, UpstreamAuth = auth, MaxContextLength = 8192 };
    }

    private sealed class StubChatHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
