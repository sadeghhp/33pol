using Pol33.Core.Models;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class GatewayErrorRepositoryTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AppendBatchAsync_RoundTripsEveryField()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(AppendBatchAsync_RoundTripsEveryField));
        var sut = new GatewayErrorRepository(db);

        var record = Error("err_1", "fp1", Base) with
        {
            ExceptionType = "System.TimeoutException",
            StackTrace = "at Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync()",
            Method = "POST",
            Path = "/v1/chat/completions",
            RouteKind = "chat",
            UpstreamTarget = "http://upstream:8000",
            Outcome = "upstream_timeout",
            TenantId = "tenant-a",
            ApiKeyId = "key-1",
            RequestId = "req_1",
            DurationMs = 1234.5,
            UpstreamBodySnippet = "{\"error\":\"overloaded\"}",
            Hint = "Check the upstream is loaded.",
        };

        await sut.AppendBatchAsync([record]);

        var loaded = await sut.GetAsync("err_1");
        loaded.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task AppendBatchAsync_IgnoresAnEmptyBatch()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(AppendBatchAsync_IgnoresAnEmptyBatch));
        var sut = new GatewayErrorRepository(db);

        await sut.AppendBatchAsync([]);

        (await sut.QueryAsync(new GatewayErrorQuery())).Total.Should().Be(0);
    }

    [Fact]
    public async Task QueryAsync_ReportsTheMatchedTotalNotThePageLength()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryAsync_ReportsTheMatchedTotalNotThePageLength));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync([.. Enumerable.Range(0, 25).Select(i => Error($"err_{i}", "fp1", Base.AddMinutes(i)))]);

        var page = await sut.QueryAsync(new GatewayErrorQuery { Limit = 10 });

        page.Items.Should().HaveCount(10);
        page.Total.Should().Be(25);
        page.Source.Should().Be(GatewayErrorSources.Database);
    }

    [Fact]
    public async Task QueryAsync_OrdersNewestFirstAndPagesStably()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryAsync_OrdersNewestFirstAndPagesStably));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync([.. Enumerable.Range(0, 10).Select(i => Error($"err_{i}", "fp1", Base.AddMinutes(i)))]);

        var first = await sut.QueryAsync(new GatewayErrorQuery { Limit = 4 });
        var second = await sut.QueryAsync(new GatewayErrorQuery { Limit = 4, Offset = 4 });

        first.Items[0].Id.Should().Be("err_9");
        second.Items[0].Id.Should().Be("err_5");
        first.Items.Select(i => i.Id).Should().NotIntersectWith(second.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task QueryAsync_AppliesEveryFilter()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryAsync_AppliesEveryFilter));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(
        [
            Error("err_a", "fp-a", Base) with
            {
                ModelId = "gpt-4o", StatusCode = 504, EventCode = "upstream_timeout",
                TenantId = "t1", RequestId = "req_a", Message = "timed out talking to upstream",
            },
            Error("err_b", "fp-b", Base.AddHours(1)) with
            {
                ModelId = "claude", StatusCode = 502, EventCode = "upstream_error",
                TenantId = "t2", RequestId = "req_b", Message = "bad gateway",
            },
        ]);

        await AssertSingle(sut, new GatewayErrorQuery { ModelId = "gpt-4o" }, "err_a");
        await AssertSingle(sut, new GatewayErrorQuery { StatusCode = 502 }, "err_b");
        await AssertSingle(sut, new GatewayErrorQuery { EventCode = "upstream_timeout" }, "err_a");
        await AssertSingle(sut, new GatewayErrorQuery { TenantId = "t2" }, "err_b");
        await AssertSingle(sut, new GatewayErrorQuery { RequestId = "req_a" }, "err_a");
        await AssertSingle(sut, new GatewayErrorQuery { Fingerprint = "fp-b" }, "err_b");
        await AssertSingle(sut, new GatewayErrorQuery { Search = "timed out" }, "err_a");
        await AssertSingle(sut, new GatewayErrorQuery { From = Base.AddMinutes(30) }, "err_b");
        await AssertSingle(sut, new GatewayErrorQuery { To = Base.AddMinutes(30) }, "err_a");
    }

    [Fact]
    public async Task QueryAsync_LevelFilterIsAFloor()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryAsync_LevelFilterIsAFloor));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(
        [
            Error("err_w", "fp-w", Base) with { Level = "Warning" },
            Error("err_e", "fp-e", Base) with { Level = "Error" },
            Error("err_c", "fp-c", Base) with { Level = "Critical" },
        ]);

        var page = await sut.QueryAsync(new GatewayErrorQuery { MinimumLevel = GatewayLogLevel.Error });

        page.Items.Select(i => i.Id).Should().BeEquivalentTo(["err_e", "err_c"]);
    }

    [Fact]
    public async Task QueryGroupsAsync_PagesStablyWhenGroupsShareATimestamp()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryGroupsAsync_PagesStablyWhenGroupsShareATimestamp));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(Enumerable.Range(0, 120)
            .Select(i => Error($"err_{i:D3}", $"fp-{i:D3}", Base))
            .ToList());

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < 120; offset += 50)
        {
            var page = await sut.QueryGroupsAsync(new GatewayErrorQuery { Limit = 50, Offset = offset });
            foreach (var group in page.Items)
            {
                seen.Add(group.Fingerprint).Should().BeTrue($"{group.Fingerprint} appeared on two pages");
            }
        }

        seen.Should().HaveCount(120, "no group may vanish between pages");
    }

    [Fact]
    public async Task QueryAsync_SearchMatchesHintAndStackTrace()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryAsync_SearchMatchesHintAndStackTrace));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(
        [
            Error("err_1", "fp-a", Base) with { Hint = "Nothing is listening on the model's URL." },
            Error("err_2", "fp-b", Base) with { StackTrace = "at Pol33.Proxy.Forwarding.InferenceHttpForwarder.SendAsync" },
            Error("err_3", "fp-c", Base),
        ]);

        (await sut.QueryAsync(new GatewayErrorQuery { Search = "listening" })).Total.Should().Be(1);
        (await sut.QueryAsync(new GatewayErrorQuery { Search = "InferenceHttpForwarder" })).Total.Should().Be(1);
    }

    [Fact]
    public async Task QueryGroupsAsync_AggregatesByFingerprintWithTheNewestSample()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryGroupsAsync_AggregatesByFingerprintWithTheNewestSample));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(
        [
            Error("err_1", "fp-a", Base) with { Message = "oldest", RequestId = "req_old" },
            Error("err_2", "fp-a", Base.AddMinutes(5)) with { Message = "newest", RequestId = "req_new" },
            Error("err_3", "fp-b", Base.AddMinutes(2)),
        ]);

        var groups = await sut.QueryGroupsAsync(new GatewayErrorQuery());

        groups.Total.Should().Be(2);
        groups.OccurrenceTotal.Should().Be(3);

        var groupA = groups.Items.Single(g => g.Fingerprint == "fp-a");
        groupA.Count.Should().Be(2);
        groupA.FirstSeen.Should().Be(Base);
        groupA.LastSeen.Should().Be(Base.AddMinutes(5));
        // The newest occurrence is the sample, so the detail panel shows the current stack trace
        // and a request id that is still in the live feed.
        groupA.Message.Should().Be("newest");
        groupA.LastRequestId.Should().Be("req_new");
    }

    [Fact]
    public async Task QueryGroupsAsync_SortsByCountWhenAsked()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryGroupsAsync_SortsByCountWhenAsked));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(
        [
            Error("err_rare", "fp-rare", Base.AddMinutes(30)),
            .. Enumerable.Range(0, 4).Select(i => Error($"err_c{i}", "fp-common", Base.AddMinutes(i))),
        ]);

        var byCount = await sut.QueryGroupsAsync(new GatewayErrorQuery { Sort = GatewayErrorSort.Count });
        var byLastSeen = await sut.QueryGroupsAsync(new GatewayErrorQuery { Sort = GatewayErrorSort.LastSeen });

        byCount.Items[0].Fingerprint.Should().Be("fp-common");
        byLastSeen.Items[0].Fingerprint.Should().Be("fp-rare");
    }

    [Fact]
    public async Task QueryGroupsAsync_ReturnsAnEmptyPagePastTheEnd()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(QueryGroupsAsync_ReturnsAnEmptyPagePastTheEnd));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync([Error("err_1", "fp-a", Base)]);

        var groups = await sut.QueryGroupsAsync(new GatewayErrorQuery { Offset = 100 });

        groups.Items.Should().BeEmpty();
        groups.Total.Should().Be(1);
    }

    [Fact]
    public async Task GetFacetsAsync_CountsPresentValuesWithinTheWindow()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetFacetsAsync_CountsPresentValuesWithinTheWindow));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(
        [
            Error("err_1", "fp-a", Base) with { ModelId = "gpt-4o", StatusCode = 502, EventCode = "upstream_error" },
            Error("err_2", "fp-b", Base.AddMinutes(1)) with { ModelId = "gpt-4o", StatusCode = 504, EventCode = "upstream_timeout" },
            Error("err_3", "fp-c", Base.AddDays(30)) with { ModelId = "outside-window", StatusCode = 500 },
        ]);

        var facets = await sut.GetFacetsAsync(Base.AddMinutes(-1), Base.AddMinutes(10));

        facets.Models.Should().ContainSingle();
        facets.Models[0].Value.Should().Be("gpt-4o");
        facets.Models[0].Count.Should().Be(2);
        facets.Statuses.Should().HaveCount(2);
        facets.Codes.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForAnUnknownId()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetAsync_ReturnsNullForAnUnknownId));
        var sut = new GatewayErrorRepository(db);

        (await sut.GetAsync("err_missing")).Should().BeNull();
        (await sut.GetAsync("")).Should().BeNull();
    }

    /// <summary>
    /// Also proves the delete path avoids ExecuteDelete, which the InMemory provider does not support.
    /// </summary>
    [Fact]
    public async Task DeleteAllAsync_RemovesEverything()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(DeleteAllAsync_RemovesEverything));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync([.. Enumerable.Range(0, 30).Select(i => Error($"err_{i}", "fp1", Base.AddMinutes(i)))]);

        var deleted = await sut.DeleteAllAsync();

        deleted.Should().Be(30);
        (await sut.QueryAsync(new GatewayErrorQuery())).Total.Should().Be(0);
    }

    [Fact]
    public async Task PruneAsync_DeletesByAge()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(PruneAsync_DeletesByAge));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync(
        [
            Error("err_old", "fp1", Base.AddDays(-30)),
            Error("err_new", "fp1", Base),
        ]);

        var removed = await sut.PruneAsync(Base.AddDays(-1), maxRows: 1000);

        removed.Should().Be(1);
        (await sut.QueryAsync(new GatewayErrorQuery())).Items.Single().Id.Should().Be("err_new");
    }

    [Fact]
    public async Task PruneAsync_TrimsTheOldestPastTheRowCap()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(PruneAsync_TrimsTheOldestPastTheRowCap));
        var sut = new GatewayErrorRepository(db);
        await sut.AppendBatchAsync([.. Enumerable.Range(0, 10).Select(i => Error($"err_{i}", "fp1", Base.AddMinutes(i)))]);

        var removed = await sut.PruneAsync(Base.AddDays(-1), maxRows: 4);

        removed.Should().Be(6);
        var remaining = await sut.QueryAsync(new GatewayErrorQuery { Limit = 50 });
        remaining.Items.Select(i => i.Id).Should().BeEquivalentTo(["err_9", "err_8", "err_7", "err_6"]);
    }

    [Fact]
    public async Task OccurredAt_RoundTripsThroughTheUtcTicksConverterAndOrdersCorrectly()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(OccurredAt_RoundTripsThroughTheUtcTicksConverterAndOrdersCorrectly));
        var sut = new GatewayErrorRepository(db);

        // Written with a non-UTC offset: the converter stores the instant, so ordering must follow
        // real time rather than the wall-clock text.
        var laterInstant = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.FromHours(-5));
        var earlierInstant = new DateTimeOffset(2026, 8, 1, 13, 0, 0, TimeSpan.FromHours(2));

        await sut.AppendBatchAsync(
        [
            Error("err_earlier", "fp1", earlierInstant),
            Error("err_later", "fp1", laterInstant),
        ]);

        var page = await sut.QueryAsync(new GatewayErrorQuery());

        page.Items[0].Id.Should().Be("err_later");
        page.Items[0].OccurredAt.Should().Be(laterInstant);
    }

    private static async Task AssertSingle(GatewayErrorRepository sut, GatewayErrorQuery query, string expectedId)
    {
        var page = await sut.QueryAsync(query);
        page.Items.Should().ContainSingle(because: $"query {query} should match only {expectedId}");
        page.Items[0].Id.Should().Be(expectedId);
    }

    private static GatewayErrorRecord Error(string id, string fingerprint, DateTimeOffset occurredAt) => new()
    {
        Id = id,
        Fingerprint = fingerprint,
        OccurredAt = occurredAt,
        Level = "Error",
        Source = GatewayErrorSourceNames.Proxy,
        Category = "ModelRouterMiddleware",
        EventCode = "upstream_error",
        Message = "Upstream returned 502 for model 'gpt-4o'.",
        StatusCode = 502,
        ModelId = "gpt-4o",
    };
}
