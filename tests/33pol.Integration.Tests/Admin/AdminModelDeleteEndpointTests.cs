using System.Net;
using System.Net.Http.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

public sealed class AdminModelDeleteEndpointTests
{
    [Fact]
    public async Task DeleteModel_WithSlashInId_EncodedInUrl_RemovesModel()
    {
        const string adminKey = "sk-33pol-integration-admin-key";
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", adminKey);

        const string modelId = "vendor/slash-delete-test";
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
                }
            });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var delete = await client.DeleteAsync(
            "/admin/api/models/" + Uri.EscapeDataString(modelId));
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await delete.Content.ReadFromJsonAsync<MutationPayload>();
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();

        var list = await client.GetFromJsonAsync<ModelListItem[]>("admin/api/models");
        list.Should().NotBeNull();
        list!.Select(i => i.Model.Id).Should().NotContain(modelId);
    }

    private sealed record MutationPayload(bool Success, string Message);

    private sealed record ModelListItem(ModelPayload Model);

    private sealed record ModelPayload(string Id);
}
