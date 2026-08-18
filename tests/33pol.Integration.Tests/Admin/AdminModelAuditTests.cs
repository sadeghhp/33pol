using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pol33.Core.Abstractions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Model writes through the admin API leave an audit trail with the caller's identity — like every
/// other admin mutation — and the secret-store and pricing side effects those writes produce carry
/// the same actor instead of a null one.
/// </summary>
public sealed class AdminModelAuditTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    [Fact]
    public async Task ModelCreateUpdateDelete_AreAuditedWithTheCallersIdentity()
    {
        var audit = new RecordingAuditLogger();
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuditLogger>();
                services.AddSingleton<IAuditLogger>(audit);
            }));
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

        const string id = "audited-model";
        var create = await client.PostAsJsonAsync("/admin/api/models", new
        {
            model = new { id, url = "http://upstream.test", aliases = Array.Empty<string>() },
            apiKey = "sk-upstream-secret-value",
            pricing = new { inputPricePerMillionTokens = 1.5m, outputPricePerMillionTokens = 3m },
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK, await create.Content.ReadAsStringAsync());

        var update = await client.PatchAsJsonAsync("/admin/api/models/" + id, new
        {
            model = new { id, url = "http://upstream-2.test", aliases = new[] { "audited-alias" } },
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        var delete = await client.DeleteAsync("/admin/api/models/" + id);
        delete.StatusCode.Should().Be(HttpStatusCode.OK, await delete.Content.ReadAsStringAsync());

        var actions = audit.Entries.Select(e => e.Action).ToList();
        actions.Should().Contain(["model.create", "model.update", "model.delete"]);
        actions.Should().Contain("upstream_secret.updated");
        actions.Should().Contain("model.pricing.update");

        // Every entry the model write produced names the operator key that made it, including the
        // secret-store and pricing side effects the provisioning service audits on its own.
        var modelEntries = audit.Entries
            .Where(e => e.Action.StartsWith("model.", StringComparison.Ordinal) ||
                        e.Action.StartsWith("upstream_secret.", StringComparison.Ordinal))
            .ToList();
        modelEntries.Should().NotBeEmpty();
        modelEntries.Should().OnlyContain(e => !string.IsNullOrEmpty(e.Entry.TenantId));
        modelEntries.Should().OnlyContain(e => !string.IsNullOrEmpty(e.Entry.ApiKeyId));

        var created = audit.Entries.Single(e => e.Action == "model.create");
        var createdDetails = JsonSerializer.SerializeToElement(created.Entry.Details);
        createdDetails.GetProperty("modelId").GetString().Should().Be(id);
        createdDetails.GetProperty("url").GetString().Should().Be("http://upstream.test");
        JsonSerializer.Serialize(created.Entry.Details).Should().NotContain("sk-upstream-secret-value");

        var deleted = audit.Entries.Single(e => e.Action == "model.delete");
        JsonSerializer.SerializeToElement(deleted.Entry.Details).GetProperty("modelId").GetString().Should().Be(id);
    }

    [Fact]
    public async Task RejectedModelCreate_IsNotAuditedAsCreated()
    {
        var audit = new RecordingAuditLogger();
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuditLogger>();
                services.AddSingleton<IAuditLogger>(audit);
            }));
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

        var rejected = await client.PostAsJsonAsync("/admin/api/models", new
        {
            model = new { id = "no-url-model", url = "", aliases = Array.Empty<string>() },
        });
        rejected.IsSuccessStatusCode.Should().BeFalse();

        audit.Entries.Should().NotContain(e => e.Action == "model.create");
    }

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        private readonly ConcurrentQueue<(string Action, AuditLogEntry Entry)> _entries = new();

        public IReadOnlyList<(string Action, AuditLogEntry Entry)> Entries => [.. _entries];

        public void LogAdminAction(string action, AuditLogEntry entry) => _entries.Enqueue((action, entry));
    }
}
