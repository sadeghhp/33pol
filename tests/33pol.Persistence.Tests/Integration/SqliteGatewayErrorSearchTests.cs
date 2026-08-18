using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Models;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// The Errors search box against real SQLite. The InMemory provider implements T-SQL bracket classes
/// in LIKE, which SQLite does not: an escape written as <c>[_]</c> could never match on SQLite, so a
/// search for a request id (every one contains '_') silently returned nothing in Production while
/// the InMemory suite stayed green. These pin the backslash-escaped translation.
/// </summary>
public sealed class SqliteGatewayErrorSearchTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    private static async Task<SqliteConnection> MigratedKeepAliveAsync(string connectionString)
    {
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        await db.Database.MigrateAsync();
        return keepAlive;
    }

    private static GatewayErrorRecord Error(string id, string message, string? requestId = null) => new()
    {
        Id = id,
        Fingerprint = "fp-" + id,
        OccurredAt = Base,
        Level = "Error",
        Source = GatewayErrorSourceNames.Proxy,
        Category = "ModelRouterMiddleware",
        EventCode = "upstream_error",
        Message = message,
        StatusCode = 502,
        ModelId = "gpt-4o",
        RequestId = requestId,
    };

    private static async Task<IReadOnlyList<string>> SearchAsync(GatewayErrorRepository sut, string search)
    {
        var page = await sut.QueryAsync(new GatewayErrorQuery { Search = search });
        return page.Items.Select(i => i.Id).ToList();
    }

    [Fact]
    public async Task Search_ByRequestIdContainingUnderscore_FindsTheError()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(
        [
            Error("err_1", "Upstream returned 502.", requestId: "req_abc123"),
            Error("err_2", "Upstream returned 502.", requestId: "req_xyz789"),
            Error("err_3", "Upstream returned 502.", requestId: "reqXabc123"),
        ]);

        (await SearchAsync(sut, "req_abc")).Should().BeEquivalentTo(["err_1"]);
        (await SearchAsync(sut, "req_")).Should().BeEquivalentTo(["err_1", "err_2"], "'_' is a literal, not a one-character wildcard");
    }

    [Fact]
    public async Task Search_TreatsPercentAndBackslashAsLiterals()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(
        [
            Error("err_pct", "Budget at 100% for tenant."),
            Error("err_plain", "Budget at 100 for tenant."),
            Error("err_bs", @"Path C:\models\gpt failed."),
            Error("err_bracket", "Bracketed [_] literal."),
        ]);

        (await SearchAsync(sut, "100%")).Should().BeEquivalentTo(["err_pct"], "'%' must not act as a wildcard");
        (await SearchAsync(sut, @"C:\models")).Should().BeEquivalentTo(["err_bs"], "the escape character itself must be escaped");
        (await SearchAsync(sut, "[_]")).Should().BeEquivalentTo(["err_bracket"], "SQLite LIKE has no bracket classes");
        (await SearchAsync(sut, "for tenant")).Should().BeEquivalentTo(["err_pct", "err_plain"]);
    }
}
