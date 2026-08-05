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
    [InlineData("http://localhost:8080", "http://localhost:8080/v1/rerank")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/v1/rerank")]
    public void BuildRerankUri_NormalizesBase(string baseUrl, string expected)
    {
        AdminModelTestService.BuildRerankUri(baseUrl).ToString().Should().Be(expected);
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

    [Fact]
    public async Task TestAsync_RerankModel_SendsRerankPayload()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.OK,
            """
            {
              "results": [
                { "index": 0, "relevance_score": 0.87, "document": { "text": "test document" } }
              ]
            }
            """);
        var model = CreateModel("reranker", "http://upstream.test");
        model.Capabilities = ["rerank"];
        var service = CreateService(handler, model);

        var result = await service.TestAsync("reranker", new() { Prompt = "test query" });

        result.Ok.Should().BeTrue();
        result.Content.Should().Be("0.87");
        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/rerank");
        handler.LastRequestBody.Should().Contain("\"query\":\"test query\"");
        handler.LastRequestBody.Should().Contain("\"documents\":[\"test document\"]");
    }

    [Theory]
    [InlineData("http://localhost:2215", "http://localhost:2215/v1/embeddings")]
    [InlineData("http://localhost:2215/", "http://localhost:2215/v1/embeddings")]
    [InlineData("https://openrouter.ai/api", "https://openrouter.ai/api/v1/embeddings")]
    public void BuildEmbeddingsUri_NormalizesBase(string baseUrl, string expected)
    {
        AdminModelTestService.BuildEmbeddingsUri(baseUrl).ToString().Should().Be(expected);
    }

    /// <summary>
    /// The '/v1' + '/v1/chat/completions' double-prefix is the single most common cause of an
    /// instant 404 from an otherwise healthy upstream, so the failure must name it explicitly.
    /// </summary>
    [Fact]
    public async Task TestAsync_UpstreamNotFound_WithV1SuffixedUrl_HintsAtDuplicatedPrefix()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotFound, "Not Found");
        var model = CreateModel("microsoft/harrier-oss-v1-27b", "http://localhost:2215/v1");
        var service = CreateService(handler, model);

        var result = await service.TestAsync("microsoft/harrier-oss-v1-27b", null);

        result.Ok.Should().BeFalse();
        result.StatusCode.Should().Be(404);
        result.Hint.Should().Contain("already ends in '/v1'");
        result.Hint.Should().Contain("http://localhost:2215");
        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/v1/chat/completions");
    }

    [Fact]
    public async Task TestAsync_UpstreamNotFound_WithRootUrl_HintsAtRouteOrModelName()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotFound, "Not Found");
        var model = CreateModel("microsoft/harrier-oss-v1-27b", "http://localhost:2215");
        var service = CreateService(handler, model);

        var result = await service.TestAsync("microsoft/harrier-oss-v1-27b", null);

        result.Hint.Should().Contain("/v1/chat/completions");
        result.Hint.Should().Contain("microsoft/harrier-oss-v1-27b");
        result.Hint.Should().NotContain("already ends in");
    }

    [Fact]
    public async Task TestAsync_UpstreamFailure_IsRecordedInTheLogStore()
    {
        var handler = new CapturingHandler(HttpStatusCode.NotFound, """{"detail":"model not found"}""");
        var model = CreateModel("demo", "http://localhost:2215");
        var service = CreateService(handler, model);

        await service.TestAsync("demo", null);

        var entry = _logs.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(nameof(GatewayLogLevel.Error));
        entry.Category.Should().Be(AdminModelTestService.LogCategory);
        entry.EventCode.Should().Be("upstream.http_404");
        entry.ModelId.Should().Be("demo");
        entry.Hint.Should().NotBeNullOrWhiteSpace();
        // The raw body is what tells "no such route" from "no such model" apart.
        entry.Detail.Should().Contain("model not found");
        entry.Detail.Should().Contain("http://localhost:2215/v1/chat/completions");
    }

    [Fact]
    public async Task TestAsync_Success_RecordsNothing()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"Hello"}}]}""");
        var service = CreateService(handler, CreateModel("demo", "http://upstream.test"));

        var result = await service.TestAsync("demo", null);

        result.Ok.Should().BeTrue();
        _logs.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task TestAsync_EmbeddingModel_PostsEmbeddingsPayload()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, EmbeddingsBody);
        var model = CreateModel("microsoft/harrier-oss-v1-27b", "http://localhost:2215");
        model.ModelType = ModelTypes.Embedding;
        var service = CreateService(handler, model);

        var result = await service.TestAsync("microsoft/harrier-oss-v1-27b", null);

        result.Ok.Should().BeTrue();
        result.ModelType.Should().Be(ModelTypes.Embedding);
        result.Endpoint.Should().Be("/v1/embeddings");
        result.Content.Should().Be("2 embeddings × 3 dimensions");
        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/embeddings");
        handler.LastRequestBody.Should().Contain("\"model\":\"microsoft/harrier-oss-v1-27b\"");
        handler.LastRequestBody.Should().Contain(
            "\"input\":[\"This is a test sentence.\",\"This sentence is used for similarity testing.\"]");
    }

    [Fact]
    public async Task TestAsync_EmbeddingModel_UsesPromptAsFirstInput()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, EmbeddingsBody);
        var model = CreateModel("embedder", "http://upstream.test");
        model.ModelType = ModelTypes.Embedding;
        var service = CreateService(handler, model);

        await service.TestAsync("embedder", new() { Prompt = "custom probe" });

        handler.LastRequestBody.Should().Contain(
            "\"input\":[\"custom probe\",\"This sentence is used for similarity testing.\"]");
    }

    /// <summary>Models registered before modelType existed must still route to the right probe.</summary>
    [Fact]
    public async Task TestAsync_EmbeddingCapabilityWithoutModelType_PostsEmbeddingsPayload()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, EmbeddingsBody);
        var model = CreateModel("legacy-embedder", "http://upstream.test");
        model.Capabilities = ["embeddings"];
        var service = CreateService(handler, model);

        var result = await service.TestAsync("legacy-embedder", null);

        result.Ok.Should().BeTrue();
        result.ModelType.Should().Be(ModelTypes.Embedding);
        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/embeddings");
    }

    [Fact]
    public async Task TestAsync_EmbeddingModel_EmptyDataIsFailure()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"data":[],"model":"embedder"}""");
        var model = CreateModel("embedder", "http://upstream.test");
        model.ModelType = ModelTypes.Embedding;
        var service = CreateService(handler, model);

        var result = await service.TestAsync("embedder", null);

        result.Ok.Should().BeFalse();
        result.StatusCode.Should().Be(200);
        result.Detail.Should().Contain("embedding");
    }

    [Fact]
    public async Task TestAsync_EmbeddingModel_UpstreamErrorSurfacesDetail()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"model does not support embeddings"}}""");
        var model = CreateModel("embedder", "http://upstream.test");
        model.ModelType = ModelTypes.Embedding;
        var service = CreateService(handler, model);

        var result = await service.TestAsync("embedder", null);

        result.Ok.Should().BeFalse();
        result.StatusCode.Should().Be(400);
        result.Endpoint.Should().Be("/v1/embeddings");
        result.Detail.Should().Contain("does not support embeddings");
    }

    [Fact]
    public async Task TestAsync_TypeWithoutProbe_ReportsUnsupportedWithoutCallingUpstream()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{}");
        var model = CreateModel("video", "http://upstream.test");
        model.ModelType = ModelTypes.VideoGeneration;
        var service = CreateService(handler, model);

        var result = await service.TestAsync("video", null);

        result.Ok.Should().BeFalse();
        result.Supported.Should().BeFalse();
        result.ModelType.Should().Be(ModelTypes.VideoGeneration);
        result.Detail.Should().Contain("video-generation");
        handler.LastRequestUri.Should().BeNull();
    }

    [Fact]
    public async Task TestAsync_OcrModel_UsesChatProbe()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"pong"}}]}""");
        var model = CreateModel("ocr-model", "http://upstream.test");
        model.ModelType = ModelTypes.Ocr;
        var service = CreateService(handler, model);

        var result = await service.TestAsync("ocr-model", null);

        result.Ok.Should().BeTrue();
        result.ModelType.Should().Be(ModelTypes.Ocr);
        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/chat/completions");
    }

    [Fact]
    public void TryExtractEmbeddingSummary_RejectsRaggedOrMissingVectors()
    {
        AdminModelTestService.TryExtractEmbeddingSummary(EmbeddingsBody)
            .Should().Be("2 embeddings × 3 dimensions");
        AdminModelTestService.TryExtractEmbeddingSummary("""{"data":[{"embedding":[]}]}""")
            .Should().BeNull();
        AdminModelTestService.TryExtractEmbeddingSummary("""{"data":[{"index":0}]}""")
            .Should().BeNull();
        AdminModelTestService.TryExtractEmbeddingSummary(
            """{"data":[{"embedding":[0.1,0.2]},{"embedding":[0.1]}]}""")
            .Should().BeNull();
        AdminModelTestService.TryExtractEmbeddingSummary("not json").Should().BeNull();
    }

    private const string EmbeddingsBody =
        """
        {
          "object": "list",
          "data": [
            { "object": "embedding", "index": 0, "embedding": [0.1, 0.2, 0.3] },
            { "object": "embedding", "index": 1, "embedding": [0.4, 0.5, 0.6] }
          ],
          "model": "embedder"
        }
        """;

    /// <summary>Diagnostics recorded by the service under test; xUnit gives each test a fresh instance.</summary>
    private readonly RecordingLogStore _logs = new();

    private AdminModelTestService CreateService(
        HttpMessageHandler handler,
        params ModelConfig[] models) =>
        CreateService(handler, (IReadOnlyList<ModelConfig>)models);

    private AdminModelTestService CreateService(HttpMessageHandler handler, IReadOnlyList<ModelConfig> models)
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

        return new AdminModelTestService(registry, resolver, factory, _logs);
    }

    private sealed class RecordingLogStore : IGatewayLogStore
    {
        private readonly List<GatewayLogEntry> _entries = [];

        public int Capacity => 100;

        public IReadOnlyList<GatewayLogEntry> Entries => _entries;

        public void Record(GatewayLogEntry entry) => _entries.Add(entry);

        public IReadOnlyList<GatewayLogEntry> GetRecent(
            int limit,
            GatewayLogLevel? minimumLevel = null,
            string? search = null) =>
            _entries;

        public void Clear() => _entries.Clear();
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

    private sealed class CapturingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
