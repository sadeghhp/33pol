using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Api.Services;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class AdminModelTestEndpointTests
{
    [Fact]
    public async Task PostModelTest_WithStoredKey_ReturnsSuccess()
    {
        const string adminKey = "sk-33pol-model-test-admin";
        var chatHandler = new StubChatCompletionHandler();
        using var factory = CreateFactory(adminKey, chatHandler);
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var modelId = "test-" + Guid.NewGuid().ToString("N")[..8];
        var create = await client.PostAsJsonAsync(
            "/admin/api/models",
            new
            {
                model = new
                {
                    id = modelId,
                    url = "http://upstream.test",
                    aliases = Array.Empty<string>(),
                    maxContextLength = 8192
                },
                apiKey = "sk-upstream-test-key-1234567890abcdef"
            });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync(
            $"/admin/api/models/{modelId}/test",
            new { prompt = "ping", maxTokens = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ModelTestPayload>();
        body.Should().NotBeNull();
        body!.Ok.Should().BeTrue();
        body.ModelId.Should().Be(modelId);
        body.Content.Should().Be("pong");
        body.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
        chatHandler.LastAuthorization.Should().Be("Bearer sk-upstream-test-key-1234567890abcdef");
    }

    [Fact]
    public async Task PostModelTest_UnknownModel_Returns404()
    {
        const string adminKey = "sk-33pol-model-test-404";
        using var factory = CreateFactory(adminKey, new StubChatCompletionHandler());
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var response = await client.PostAsJsonAsync("/admin/api/models/not-registered/test", new { });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostModelTest_MissingStoredKey_Returns400()
    {
        const string adminKey = "sk-33pol-model-test-400";
        using var factory = CreateFactory(adminKey, new StubChatCompletionHandler());
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var modelId = "needs-key-" + Guid.NewGuid().ToString("N")[..8];
        var create = await client.PostAsJsonAsync(
            "/admin/api/models",
            new
            {
                model = new
                {
                    id = modelId,
                    url = "https://api.openai.com/v1",
                    aliases = Array.Empty<string>(),
                    maxContextLength = 8192,
                    upstreamAuth = new { type = "bearer", envVar = "TEST_MISSING_UPSTREAM_API_KEY" }
                }
            });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync($"/admin/api/models/{modelId}/test", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("no API key");
        json.Should().NotContain("sk-");
    }

    [Fact]
    public async Task PostModelTest_WithoutAdminKey_Returns401()
    {
        using var factory = CreateFactory("sk-33pol-model-test-unauth", new StubChatCompletionHandler());
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/admin/api/models/local-mock/test", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostModelTest_EncodedSlashInRouteId_ResolvesRegisteredModel()
    {
        const string adminKey = "sk-33pol-model-test-encoded-slash";
        var chatHandler = new StubChatCompletionHandler();
        using var factory = CreateFactory(adminKey, chatHandler);
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var modelId = $"vendor/sub-model-{Guid.NewGuid():N}:free";
        var create = await client.PostAsJsonAsync(
            "/admin/api/models",
            new
            {
                model = new
                {
                    id = modelId,
                    url = "http://upstream.test",
                    aliases = Array.Empty<string>(),
                    maxContextLength = 8192
                },
                apiKey = "sk-upstream-test-key-1234567890abcdef"
            });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var encodedId = Uri.EscapeDataString(modelId);
        var response = await client.PostAsJsonAsync(
            $"/admin/api/models/{encodedId}/test",
            new { prompt = "ping", maxTokens = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ModelTestPayload>();
        body.Should().NotBeNull();
        body!.Ok.Should().BeTrue();
        body.ModelId.Should().Be(modelId);
    }

    [Fact]
    public async Task PostModelTest_UpstreamUnauthorized_ReturnsOkFalse()
    {
        const string adminKey = "sk-33pol-model-test-upstream-401";
        var chatHandler = new StubChatCompletionHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""");
        using var factory = CreateFactory(adminKey, chatHandler);
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var modelId = "bad-upstream-" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync(
            "/admin/api/models",
            new
            {
                model = new
                {
                    id = modelId,
                    url = "http://upstream.test",
                    aliases = Array.Empty<string>(),
                    maxContextLength = 8192
                },
                apiKey = "sk-upstream-bad-key-1234567890abcdef"
            });

        var response = await client.PostAsJsonAsync($"/admin/api/models/{modelId}/test", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ModelTestPayload>();
        body!.Ok.Should().BeFalse();
        body.StatusCode.Should().Be(401);
        body.Detail.Should().Contain("bad key");
    }

    /// <summary>
    /// The whole point of model types: an embedding model must be probed on /v1/embeddings, and the
    /// type must survive the round-trip through the model registry.
    /// </summary>
    [Fact]
    public async Task PostModelTest_EmbeddingModel_ProbesEmbeddingsEndpoint()
    {
        const string adminKey = "sk-33pol-model-test-embeddings";
        var handler = new StubUpstreamHandler(
            HttpStatusCode.OK,
            """{"object":"list","data":[{"embedding":[0.1,0.2]},{"embedding":[0.3,0.4]}]}""");
        using var factory = CreateFactory(adminKey, handler);
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var modelId = "embedder-" + Guid.NewGuid().ToString("N")[..8];
        var create = await client.PostAsJsonAsync(
            "/admin/api/models",
            new
            {
                model = new
                {
                    id = modelId,
                    url = "http://upstream.test",
                    aliases = Array.Empty<string>(),
                    maxContextLength = 8192,
                    modelType = "embedding"
                },
                apiKey = "sk-upstream-test-key-1234567890abcdef"
            });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync($"/admin/api/models/{modelId}/test", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ModelTestPayload>();
        body!.Ok.Should().BeTrue();
        body.ModelType.Should().Be("embedding");
        body.Endpoint.Should().Be("/v1/embeddings");
        body.Content.Should().Be("2 embeddings \u00d7 2 dimensions");
        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/embeddings");
        handler.LastRequestBody.Should().Contain(
            "\"input\":[\"This is a test sentence.\",\"This sentence is used for similarity testing.\"]");
    }

    [Fact]
    public async Task PostModelAdd_UnknownModelType_Returns400()
    {
        const string adminKey = "sk-33pol-model-type-invalid";
        using var factory = CreateFactory(adminKey, new StubChatCompletionHandler());
        using var client = await CreateAdminClientAsync(factory, adminKey);

        var response = await client.PostAsJsonAsync(
            "/admin/api/models",
            new
            {
                model = new
                {
                    id = "bad-type-" + Guid.NewGuid().ToString("N")[..8],
                    url = "http://upstream.test",
                    aliases = Array.Empty<string>(),
                    maxContextLength = 8192,
                    modelType = "teleportation"
                }
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("modelType");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string adminApiKey,
        HttpMessageHandler chatHandler)
    {
        return GatewayWebApplicationFactory.CreateWithInMemoryDatabase(adminApiKey: adminApiKey)
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddHttpClient(AdminModelTestService.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => chatHandler);
                });
            });
    }

    private static async Task<HttpClient> CreateAdminClientAsync(WebApplicationFactory<Program> factory, string adminKey)
    {
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
        return client;
    }

    private sealed record ModelTestPayload(
        bool Ok,
        string ModelId,
        string? ModelType,
        string? Endpoint,
        bool Supported,
        long LatencyMs,
        int? StatusCode,
        string? Detail,
        string? Content);

    /// <summary>Captures the request so a test can assert which upstream route the probe chose.</summary>
    private sealed class StubUpstreamHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
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
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubChatCompletionHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StubChatCompletionHandler(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string? body = null)
        {
            _statusCode = statusCode;
            _body = body ??
                    """
                    {
                      "choices": [
                        { "message": { "content": "pong" } }
                      ]
                    }
                    """;
        }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
