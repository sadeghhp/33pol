using Pol33.Core.Models;
using Pol33.Observability.Diagnostics;

namespace Pol33.Observability.Tests.Diagnostics;

public sealed class InMemoryGatewayLogStoreTests
{
    [Fact]
    public void GetRecent_ReturnsNewestFirst()
    {
        var store = new InMemoryGatewayLogStore();

        store.Record(Entry("first"));
        store.Record(Entry("second"));

        store.GetRecent(10).Select(e => e.Message).Should().Equal("second", "first");
    }

    [Fact]
    public void Record_DropsOldestOnceCapacityIsReached()
    {
        var store = new InMemoryGatewayLogStore(capacity: 2, TimeProvider.System);

        store.Record(Entry("one"));
        store.Record(Entry("two"));
        store.Record(Entry("three"));

        store.GetRecent(10).Select(e => e.Message).Should().Equal("three", "two");
    }

    /// <summary>
    /// One badly configured upstream can fire the same error hundreds of times a second. Without
    /// coalescing it would evict every other diagnostic from the buffer before an operator looked.
    /// </summary>
    [Fact]
    public void Record_CoalescesIdenticalConsecutiveEvents()
    {
        var store = new InMemoryGatewayLogStore();

        store.Record(Entry("upstream down"));
        store.Record(Entry("upstream down"));
        store.Record(Entry("upstream down"));

        var entry = store.GetRecent(10).Should().ContainSingle().Subject;
        entry.Repeats.Should().Be(3);
        entry.LastTimestampUtc.Should().BeOnOrAfter(entry.TimestampUtc);
    }

    [Fact]
    public void Record_DoesNotCoalesceOutsideTheWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryGatewayLogStore(capacity: 10, clock);

        store.Record(Entry("upstream down"));
        clock.Advance(InMemoryGatewayLogStore.CoalesceWindow + TimeSpan.FromSeconds(1));
        store.Record(Entry("upstream down"));

        store.GetRecent(10).Should().HaveCount(2);
    }

    [Fact]
    public void Record_DoesNotCoalesceDifferentModels()
    {
        var store = new InMemoryGatewayLogStore();

        store.Record(Entry("upstream down", modelId: "a"));
        store.Record(Entry("upstream down", modelId: "b"));

        store.GetRecent(10).Should().HaveCount(2);
    }

    [Fact]
    public void GetRecent_MinimumLevelActsAsAFloor()
    {
        var store = new InMemoryGatewayLogStore();

        store.Record(Entry("info", GatewayLogLevel.Info));
        store.Record(Entry("warned", GatewayLogLevel.Warning));
        store.Record(Entry("failed", GatewayLogLevel.Error));

        store.GetRecent(10, GatewayLogLevel.Warning).Select(e => e.Message)
            .Should().Equal("failed", "warned");
        store.GetRecent(10, GatewayLogLevel.Error).Select(e => e.Message)
            .Should().Equal("failed");
    }

    [Theory]
    [InlineData("harrier")]
    [InlineData("HARRIER")]
    [InlineData("upstream.http_404")]
    [InlineData("check the url")]
    public void GetRecent_SearchMatchesAcrossFields(string needle)
    {
        var store = new InMemoryGatewayLogStore();
        store.Record(new GatewayLogEntry
        {
            Id = "log_1",
            Level = nameof(GatewayLogLevel.Error),
            Category = "ModelTest",
            EventCode = "upstream.http_404",
            Message = "Model test failed",
            Hint = "Check the URL.",
            ModelId = "microsoft/harrier-oss-v1-27b",
        });

        store.GetRecent(10, search: needle).Should().ContainSingle();
        store.GetRecent(10, search: "nothing-matches-this").Should().BeEmpty();
    }

    [Fact]
    public void Record_TruncatesOversizedDetail()
    {
        var store = new InMemoryGatewayLogStore();

        store.Record(new GatewayLogEntry
        {
            Id = "log_1",
            Level = nameof(GatewayLogLevel.Error),
            Category = "Test",
            Message = "boom",
            Detail = new string('x', InMemoryGatewayLogStore.MaxDetailLength + 500),
        });

        store.GetRecent(1)[0].Detail!.Length
            .Should().Be(InMemoryGatewayLogStore.MaxDetailLength + 1);
    }

    [Fact]
    public void Clear_EmptiesTheBuffer()
    {
        var store = new InMemoryGatewayLogStore();
        store.Record(Entry("boom"));

        store.Clear();

        store.GetRecent(10).Should().BeEmpty();
    }

    private static GatewayLogEntry Entry(
        string message,
        GatewayLogLevel level = GatewayLogLevel.Error,
        string? modelId = null) =>
        new()
        {
            Id = string.Empty,
            Level = level.ToString(),
            Category = "Test",
            EventCode = "test.event",
            Message = message,
            ModelId = modelId,
        };

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
