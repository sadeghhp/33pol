using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Observability.Diagnostics;

namespace Pol33.Observability.Tests.Diagnostics;

public sealed class InMemoryGatewayErrorStoreTests
{
    [Fact]
    public async Task Record_AssignsFingerprintAndGroupsIdenticalFailures()
    {
        var store = CreateStore();

        store.Record(Error(requestId: "req_1"));
        store.Record(Error(requestId: "req_2"));
        store.Record(Error(requestId: "req_3"));

        var groups = await store.QueryGroupsAsync(new GatewayErrorQuery());

        groups.Items.Should().ContainSingle();
        groups.Items[0].Count.Should().Be(3);
        groups.Items[0].Fingerprint.Should().NotBeNullOrWhiteSpace();
        groups.OccurrenceTotal.Should().Be(3);
    }

    [Fact]
    public async Task Record_EvictsOldestPastCapacity()
    {
        var store = CreateStore(o => o.HotBufferCapacity = 3);

        for (var i = 0; i < 10; i++)
        {
            store.Record(Error(requestId: $"req_{i}", message: $"failure {i}"));
        }

        var page = await store.QueryAsync(new GatewayErrorQuery { Limit = 50 });

        page.Items.Should().HaveCount(3);
        page.Items[0].Message.Should().Be("failure 9");
    }

    [Fact]
    public async Task GroupTotals_SurviveRingEviction()
    {
        // The whole point of tracking aggregates separately: without them, a fault that fired
        // thousands of times reports "1 occurrence, first seen seconds ago" once its older rows
        // are evicted — which is worse than useless mid-incident.
        var store = CreateStore(o => o.HotBufferCapacity = 3);

        for (var i = 0; i < 50; i++)
        {
            store.Record(Error(requestId: $"req_{i}"));
        }

        var groups = await store.QueryGroupsAsync(new GatewayErrorQuery());

        groups.Items.Should().ContainSingle();
        groups.Items[0].Count.Should().Be(50);
    }

    [Fact]
    public async Task Record_DropsTheSameFailureTwiceOnOneRequest()
    {
        // The proxy and the terminal exception handler both see a failed forward, and the log sink
        // sees the ILogger call after it. Counted naively, every upstream fault would triple.
        var store = CreateStore();

        store.Record(Error(requestId: "req_dupe"));
        store.Record(Error(requestId: "req_dupe"));

        var groups = await store.QueryGroupsAsync(new GatewayErrorQuery());
        groups.Items.Should().ContainSingle();
        groups.Items[0].Count.Should().Be(1);
    }

    /// <summary>
    /// The dedupe map used to be scanned in full on every record once it grew past 2x capacity, and
    /// during a burst of distinct requests nothing was ever stale, so it grew for the whole window.
    /// It is now hard-capped by insertion order: memory stays proportional to capacity and a
    /// repeat inside the window is still recognised for the most recent requests.
    /// </summary>
    [Fact]
    public async Task Record_UnderADistinctRequestStorm_KeepsDedupeMapBoundedAndStillDedupesRecent()
    {
        var store = CreateStore(o => o.HotBufferCapacity = 20);

        for (var i = 0; i < 5_000; i++)
        {
            store.Record(Error(requestId: $"req_{i}"));
        }

        store.RecentlySeenCount.Should().BeLessThanOrEqualTo(store.Capacity * 2);

        // The newest request is still inside the cap: a repeat is dropped.
        store.Record(Error(requestId: "req_4999"));
        var groups = await store.QueryGroupsAsync(new GatewayErrorQuery());
        groups.Items[0].Count.Should().Be(5_000);
    }

    [Fact]
    public async Task Record_DropsALogSourcedCopyOfAFailureTheProxyAlreadyReported()
    {
        // The log entry is the same fault seen from further away. Keeping both would show one
        // upstream failure as two groups, one of them missing the model and upstream.
        var store = CreateStore();

        store.Record(Error(requestId: "req_1"));
        store.Record(Error(requestId: "req_1", message: "Forwarder error Request for model local-mock") with
        {
            Source = GatewayErrorSourceNames.Log,
            Category = "ModelRouterMiddleware",
            ModelId = null,
        });

        var groups = await store.QueryGroupsAsync(new GatewayErrorQuery());

        groups.Items.Should().ContainSingle();
        groups.Items[0].ModelId.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task Record_KeepsALogSourcedFailureOnItsOwnRequest()
    {
        var store = CreateStore();

        store.Record(Error(requestId: "req_1"));
        store.Record(Error(requestId: "req_2", message: "background job failed") with
        {
            Source = GatewayErrorSourceNames.Log,
        });

        (await store.QueryGroupsAsync(new GatewayErrorQuery())).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Record_KeepsBothWhenThereIsNoRequestIdToCorrelateOn()
    {
        var store = CreateStore();

        store.Record(Error(requestId: null));
        store.Record(Error(requestId: null));

        var groups = await store.QueryGroupsAsync(new GatewayErrorQuery());
        groups.Items[0].Count.Should().Be(2);
    }

    [Fact]
    public async Task Record_EvictsTheColdestFingerprintPastTheCap()
    {
        var store = CreateStore(o =>
        {
            o.MaxTrackedFingerprints = 2;
            o.HotBufferCapacity = 100;
        });

        store.Record(Error(requestId: "a", message: "first"));
        store.Record(Error(requestId: "b", message: "second"));
        store.Record(Error(requestId: "c", message: "third"));

        var groups = await store.QueryGroupsAsync(new GatewayErrorQuery());

        groups.Items.Should().HaveCount(2);
        groups.Items.Select(g => g.Message).Should().NotContain("first");
    }

    [Fact]
    public async Task Record_RedactsSecretsBeforeTheyReachTheBuffer()
    {
        var store = CreateStore();

        store.Record(Error(message: "upstream rejected Authorization: Bearer sk-supersecretvalue") with
        {
            UpstreamTarget = "http://user:pass@upstream:8000/v1?api_key=leaked",
        });

        var page = await store.QueryAsync(new GatewayErrorQuery());

        page.Items[0].Message.Should().NotContain("sk-supersecretvalue");
        page.Items[0].UpstreamTarget.Should().Be("http://upstream:8000/v1");
    }

    [Fact]
    public async Task QueryAsync_AppliesEveryFilter()
    {
        var store = CreateStore();
        store.Record(Error(requestId: "r1", message: "timeout talking to upstream") with
        {
            ModelId = "gpt-4o",
            StatusCode = 504,
            EventCode = "upstream_timeout",
        });
        store.Record(Error(requestId: "r2", message: "bad gateway") with
        {
            ModelId = "claude",
            StatusCode = 502,
            EventCode = "upstream_error",
        });

        (await store.QueryAsync(new GatewayErrorQuery { ModelId = "gpt-4o" })).Items.Should().ContainSingle();
        (await store.QueryAsync(new GatewayErrorQuery { StatusCode = 502 })).Items.Should().ContainSingle();
        (await store.QueryAsync(new GatewayErrorQuery { EventCode = "upstream_timeout" })).Items.Should().ContainSingle();
        (await store.QueryAsync(new GatewayErrorQuery { Search = "timeout" })).Items.Should().ContainSingle();
        (await store.QueryAsync(new GatewayErrorQuery { RequestId = "r2" })).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task QueryAsync_ReportsTheMatchedTotalNotThePageLength()
    {
        var store = CreateStore();
        for (var i = 0; i < 20; i++)
        {
            store.Record(Error(requestId: $"req_{i}", message: $"failure {i}"));
        }

        var page = await store.QueryAsync(new GatewayErrorQuery { Limit = 5 });

        page.Items.Should().HaveCount(5);
        page.Total.Should().Be(20);
    }

    [Fact]
    public async Task QueryGroupsAsync_SortsByCountWhenAsked()
    {
        var store = CreateStore();
        store.Record(Error(requestId: "a1", message: "rare"));
        for (var i = 0; i < 5; i++)
        {
            store.Record(Error(requestId: $"b{i}", message: "common"));
        }

        var groups = await store.QueryGroupsAsync(new GatewayErrorQuery { Sort = GatewayErrorSort.Count });

        groups.Items[0].Message.Should().Be("common");
    }

    [Fact]
    public async Task GetFacetsAsync_ReturnsPresentValuesWithCounts()
    {
        var store = CreateStore();
        store.Record(Error(requestId: "a") with { ModelId = "gpt-4o" });
        store.Record(Error(requestId: "b", message: "other") with { ModelId = "gpt-4o" });
        store.Record(Error(requestId: "c", message: "third") with { ModelId = "claude" });

        var facets = await store.GetFacetsAsync(null, null);

        facets.Models.Should().HaveCount(2);
        facets.Models[0].Value.Should().Be("gpt-4o");
        facets.Models[0].Count.Should().Be(2);
    }

    [Fact]
    public async Task ClearAsync_EmptiesRecordsAndAggregates()
    {
        var store = CreateStore();
        store.Record(Error(requestId: "a"));
        store.Record(Error(requestId: "b"));

        var removed = await store.ClearAsync();

        removed.Should().Be(2);
        (await store.QueryAsync(new GatewayErrorQuery())).Items.Should().BeEmpty();
        // The aggregates must go too, or the group list survives a clear with its counts intact.
        (await store.QueryGroupsAsync(new GatewayErrorQuery())).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_ReturnsTheRecordById()
    {
        var store = CreateStore();
        store.Record(Error(requestId: "a"));
        var id = (await store.QueryAsync(new GatewayErrorQuery())).Items[0].Id;

        (await store.GetAsync(id)).Should().NotBeNull();
        (await store.GetAsync("err_missing")).Should().BeNull();
    }

    [Fact]
    public void Record_WhenDisabled_DoesNothing()
    {
        var store = CreateStore(o => o.Enabled = false);

        store.Record(Error(requestId: "a"));

        store.QueryAsync(new GatewayErrorQuery()).Result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentRecordAndQuery_StaySafe()
    {
        var store = CreateStore(o => o.HotBufferCapacity = 50);

        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                store.Record(Error(requestId: $"req_{w}_{i}", message: $"failure {i % 7}"));
            }
        }));

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                await store.QueryAsync(new GatewayErrorQuery());
                await store.QueryGroupsAsync(new GatewayErrorQuery());
            }
        }));

        var act = () => Task.WhenAll(writers.Concat(readers));

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// The same query must mean the same thing from either store. The database groups the
    /// occurrences inside the window; so must the memory store, or a fallback silently turns
    /// "3 in the last hour" into "4,000 since Tuesday".
    /// </summary>
    [Fact]
    public async Task QueryGroupsAsync_WithAWindow_CountsOnlyOccurrencesInsideIt()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        store.Record(Error(requestId: "req_a") with { OccurredAt = now.AddHours(-3) });
        store.Record(Error(requestId: "req_b") with { OccurredAt = now.AddMinutes(-30) });
        store.Record(Error(requestId: "req_c") with { OccurredAt = now.AddMinutes(-5) });

        var lastHour = await store.QueryGroupsAsync(new GatewayErrorQuery { From = now.AddHours(-1) });
        var allTime = await store.QueryGroupsAsync(new GatewayErrorQuery());

        var group = lastHour.Items.Should().ContainSingle().Subject;
        group.Count.Should().Be(2);
        group.FirstSeen.Should().Be(now.AddMinutes(-30));
        group.LastRequestId.Should().Be("req_c");
        lastHour.OccurrenceTotal.Should().Be(2);
        lastHour.StoredTotal.Should().Be(3, "stored is unfiltered");

        allTime.Items.Should().ContainSingle().Which.Count.Should().Be(3);
    }

    private static InMemoryGatewayErrorStore CreateStore(Action<GatewayErrorTrackingOptions>? configure = null)
    {
        var options = new GatewayErrorTrackingOptions();
        configure?.Invoke(options);
        return new InMemoryGatewayErrorStore(Options.Create(options), TimeProvider.System);
    }

    private static GatewayErrorRecord Error(
        string? requestId = "req_1",
        string message = "Upstream returned 502 for model 'gpt-4o'.") => new()
    {
        Id = $"err_{Guid.NewGuid():N}",
        Fingerprint = string.Empty,
        OccurredAt = DateTimeOffset.UtcNow,
        Level = "Error",
        Source = GatewayErrorSourceNames.Proxy,
        Category = "ModelRouterMiddleware",
        EventCode = "upstream_error",
        Message = message,
        StatusCode = 502,
        ModelId = "gpt-4o",
        RouteKind = "chat",
        RequestId = requestId,
    };
}
