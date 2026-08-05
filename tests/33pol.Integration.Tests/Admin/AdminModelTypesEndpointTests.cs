using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The admin UI loads the model-type taxonomy from the gateway rather than keeping its own copy.
/// The duplicate it used to carry had drifted to a fraction of the accepted aliases, so a model
/// typed <c>vision-ocr</c> or <c>speech-to-text</c> displayed as text generation — and the edit
/// dialog pre-selected that wrong value, silently rewriting the model on save.
/// </summary>
public sealed class AdminModelTypesEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    [Fact]
    public async Task GetModelTypes_ReturnsEveryCanonicalTypeWithItsAliases()
    {
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

        var response = await client.GetAsync("/admin/api/model-types");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var types = json.RootElement.EnumerateArray().ToList();

        types.Select(t => t.GetProperty("value").GetString())
            .Should().BeEquivalentTo(ModelTypes.All);

        foreach (var type in types)
        {
            type.GetProperty("label").GetString().Should().NotBeNullOrWhiteSpace();
            type.GetProperty("aliases").GetArrayLength()
                .Should().BeGreaterThan(0, "every canonical type is at least an alias of itself");
        }
    }

    /// <summary>
    /// Every alias the server folds must be present in the payload, so the UI can resolve exactly
    /// what the gateway resolves. These four are the ones the UI's stale copy did not know.
    /// </summary>
    [Theory]
    [InlineData("vision-ocr", ModelTypes.Ocr)]
    [InlineData("text_generation", ModelTypes.TextGeneration)]
    [InlineData("speech-to-text", ModelTypes.AudioTranscription)]
    [InlineData("text-to-image", ModelTypes.ImageGeneration)]
    [InlineData("embeddings", ModelTypes.Embedding)]
    [InlineData("reranker", ModelTypes.Rerank)]
    public async Task GetModelTypes_CarriesTheAliasesTheUiUsedToMiss(string alias, string expectedCanonical)
    {
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

        using var json = JsonDocument.Parse(
            await client.GetStringAsync("/admin/api/model-types"));

        var owning = json.RootElement.EnumerateArray()
            .Single(t => t.GetProperty("aliases").EnumerateArray()
                .Any(a => string.Equals(a.GetString(), alias, StringComparison.OrdinalIgnoreCase)));

        owning.GetProperty("value").GetString().Should().Be(expectedCanonical);
    }

    /// <summary>
    /// Types with an automated health check advertise the endpoint it calls; the rest advertise
    /// none, so the UI does not promise a chat probe for a video model.
    /// </summary>
    [Fact]
    public async Task GetModelTypes_ReportsTestEndpointsThatMatchTheProbes()
    {
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

        using var json = JsonDocument.Parse(await client.GetStringAsync("/admin/api/model-types"));

        string? EndpointFor(string value) => json.RootElement.EnumerateArray()
            .Single(t => t.GetProperty("value").GetString() == value)
            .GetProperty("testEndpoint").GetString();

        EndpointFor(ModelTypes.TextGeneration).Should().Be("/v1/chat/completions");
        EndpointFor(ModelTypes.Ocr).Should().Be("/v1/chat/completions");
        EndpointFor(ModelTypes.Embedding).Should().Be("/v1/embeddings");
        EndpointFor(ModelTypes.Rerank).Should().Be("/v1/rerank");
        EndpointFor(ModelTypes.VideoGeneration).Should().BeNull();
        EndpointFor(ModelTypes.ImageGeneration).Should().BeNull();
        EndpointFor(ModelTypes.AudioTranscription).Should().BeNull();
    }

    [Fact]
    public async Task GetModelTypes_RequiresAdminAuthorization()
    {
        using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/api/model-types");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
