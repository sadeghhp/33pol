using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The admin route lifecycle end to end, against a real SQLite engine: what an operator does in the
/// UI (add, edit, rename, delete, re-add) and what the gateway looks like after a restart.
/// </summary>
/// <remarks>
/// These cover a family of bugs that all presented the same way — "I deleted the route but adding it
/// back says it already exists". The causes were separate: a rejected write that still reached the
/// database, a name check that matched aliases, a delete that refused the last route, and a rename
/// that silently did nothing.
/// </remarks>
public sealed class AdminModelLifecycleTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    [Fact]
    public async Task DeleteThenRecreate_WithCredentialAndPricing_Succeeds()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = CreateClient(factory);

        const string id = "recreate-me";

        (await client.PostAsJsonAsync("/admin/api/models", WriteBody(id, ["recreate-alias"], withKey: true, withPricing: true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var delete = await client.DeleteAsync("/admin/api/models/" + Uri.EscapeDataString(id));
        delete.StatusCode.Should().Be(HttpStatusCode.OK, await delete.Content.ReadAsStringAsync());

        (await ListModelIdsAsync(client)).Should().NotContain(id);

        var again = await client.PostAsJsonAsync(
            "/admin/api/models",
            WriteBody(id, ["recreate-alias"], withKey: true, withPricing: true));

        again.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a deleted model id is free again: " + await again.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A name that is only an alias is reported as an alias, not as an existing model the operator
    /// cannot find in the list.
    /// </summary>
    [Fact]
    public async Task AddModel_WhoseIdIsAnotherModelsAlias_ExplainsTheAlias()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = CreateClient(factory);

        (await client.PostAsJsonAsync("/admin/api/models", WriteBody("owner-model", ["contested"])))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync("/admin/api/models", WriteBody("contested", []));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var message = await response.Content.ReadAsStringAsync();
        message.Should().Contain("alias of model 'owner-model'");
        message.Should().NotContain("already exists");
    }

    [Fact]
    public async Task DeleteLastModel_Succeeds_AndTheGatewayStillServesAdmin()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = CreateClient(factory);

        foreach (var id in await ListModelIdsAsync(client))
        {
            var delete = await client.DeleteAsync("/admin/api/models/" + Uri.EscapeDataString(id));
            delete.StatusCode.Should().Be(HttpStatusCode.OK, $"deleting '{id}': " + await delete.Content.ReadAsStringAsync());
        }

        (await ListModelIdsAsync(client)).Should().BeEmpty();

        // And the emptied registry is a state the operator can come back from.
        (await client.PostAsJsonAsync("/admin/api/models", WriteBody("fresh-start", [])))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListModelIdsAsync(client)).Should().BeEquivalentTo(["fresh-start"]);
    }

    [Fact]
    public async Task PatchModel_WithNewId_RenamesAndCarriesCredentialAndPricing()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithSqliteDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        using var client = CreateClient(factory);

        (await client.PostAsJsonAsync("/admin/api/models", WriteBody("old-name", [], withKey: true, withPricing: true)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The UI addresses the model by the id it is stored under, puts the new name in the body, and
        // echoes back the existing credential reference (which still points at the old id).
        var patch = await client.PatchAsJsonAsync("/admin/api/models/old-name", new
        {
            model = new
            {
                id = "new-name",
                url = "http://upstream.test",
                aliases = Array.Empty<string>(),
                maxContextLength = 8192,
                upstreamAuth = new { type = "bearer", secretRef = "file:model:old-name" },
            },
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());

        var models = await ListModelsAsync(client);
        models.Select(m => m.GetProperty("model").GetProperty("id").GetString()).Should().Contain("new-name");
        models.Select(m => m.GetProperty("model").GetProperty("id").GetString()).Should().NotContain("old-name");

        var renamed = models.Single(m => m.GetProperty("model").GetProperty("id").GetString() == "new-name");
        renamed.GetProperty("hasUpstreamCredential").GetBoolean()
            .Should().BeTrue("a rename must carry the upstream credential across");
        renamed.GetProperty("pricing").GetProperty("inputPricePerMillionTokens").GetDecimal()
            .Should().Be(1.5m, "a rename must carry the rate card across");
    }

    /// <summary>
    /// The corruption path: a write the gateway rejects must leave nothing behind. Before the fix the
    /// row was committed first and only rejected while swapping it into memory, which left a database
    /// that failed to load — after the next restart the gateway had no routes at all, and the next
    /// successful write deleted every remaining route.
    /// </summary>
    [Fact]
    public async Task RejectedWrite_LeavesNothingInTheDatabase_AndSurvivesRestart()
    {
        var connectionString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();

        string[] idsBeforeRestart;

        await using (var factory = CreateSharedDatabaseFactory(connectionString))
        {
            using var client = CreateClient(factory);

            (await client.PostAsJsonAsync("/admin/api/models", WriteBody("model-a", ["shared-alias"])))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var rejected = await client.PostAsJsonAsync("/admin/api/models", WriteBody("model-b", ["shared-alias"]));
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // A reload reads the database back: it must still be loadable.
            var reload = await client.PostAsync("/admin/api/config/reload", null);
            reload.StatusCode.Should().Be(HttpStatusCode.OK, await reload.Content.ReadAsStringAsync());

            idsBeforeRestart = await ListModelIdsAsync(client);
            idsBeforeRestart.Should().Contain("model-a").And.NotContain("model-b");
        }

        await using (var restarted = CreateSharedDatabaseFactory(connectionString))
        {
            using var client = CreateClient(restarted);

            var afterRestart = await ListModelIdsAsync(client);
            afterRestart.Should().BeEquivalentTo(
                idsBeforeRestart,
                "a rejected write must not cost the gateway its routes at the next startup");

            // And a later write does not wipe the routes it was unable to see.
            (await client.PostAsJsonAsync("/admin/api/models", WriteBody("added-after-restart", [])))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            (await ListModelIdsAsync(client)).Should()
                .Contain("model-a").And.Contain("added-after-restart");
        }
    }

    private static WebApplicationFactory<Program> CreateSharedDatabaseFactory(string connectionString) =>
        (WebApplicationFactory<Program>)GatewayWebApplicationFactory.Create(
            clearGatewayDatabase: false,
            configureSettings: settings =>
            {
                settings["ConnectionStrings:GatewayDb"] = connectionString;
                settings["Gateway:Bootstrap:Enabled"] = "true";
                settings["Gateway:Bootstrap:AdminApiKey"] = AdminKey;
                settings["Gateway:Bootstrap:KeyPepper"] = "integration-test-pepper";
                settings["Gateway:Security:KeyPepper"] = "integration-test-pepper";
            });

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);
        return client;
    }

    private static object WriteBody(
        string id,
        string[] aliases,
        bool withKey = false,
        bool withPricing = false) => new
        {
            model = new
            {
                id,
                url = "http://upstream.test",
                aliases,
                maxContextLength = 8192,
                modelType = "text-generation",
            },
            apiKey = withKey ? "sk-upstream-secret-value" : null,
            pricing = withPricing
                ? new { inputPricePerMillionTokens = 1.5m, outputPricePerMillionTokens = 3m }
                : null,
        };

    private static async Task<JsonElement[]> ListModelsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/admin/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static async Task<string[]> ListModelIdsAsync(HttpClient client)
    {
        var models = await ListModelsAsync(client);
        return models
            .Select(m => m.GetProperty("model").GetProperty("id").GetString()!)
            .ToArray();
    }
}
