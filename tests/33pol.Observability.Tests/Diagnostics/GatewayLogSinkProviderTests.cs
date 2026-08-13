using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Diagnostics;
using Pol33.Core.Models;
using Pol33.Observability.Diagnostics;

namespace Pol33.Observability.Tests.Diagnostics;

public sealed class GatewayLogSinkProviderTests
{
    [Fact]
    public void CreateLogger_SameCategory_ReturnsCachedInstance()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);

        var first = provider.CreateLogger("Pol33.Proxy.ModelRouterMiddleware");
        var second = provider.CreateLogger("Pol33.Proxy.ModelRouterMiddleware");

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void IsEnabled_BelowWarning_ReturnsFalse()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("Tests");

        logger.IsEnabled(LogLevel.Information).Should().BeFalse();
        logger.IsEnabled(LogLevel.None).Should().BeFalse();
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
        logger.IsEnabled(LogLevel.Error).Should().BeTrue();
        logger.IsEnabled(LogLevel.Critical).Should().BeTrue();
    }

    [Fact]
    public void Log_Warning_RecordsShortCategoryAndLevel()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("Pol33.Proxy.ModelRouterMiddleware");

        logger.LogWarning(new EventId(1, "route_failed"), "upstream timed out");

        store.Entries.Should().ContainSingle();
        store.Entries[0].Category.Should().Be("ModelRouterMiddleware");
        store.Entries[0].Level.Should().Be(nameof(GatewayLogLevel.Warning));
        store.Entries[0].EventCode.Should().Be("route_failed");
        store.Entries[0].Message.Should().Be("upstream timed out");
    }

    [Fact]
    public void Log_ErrorWithException_RecordsDetailAndHint()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("NoNamespace");

        logger.LogError(new InvalidOperationException("boom"), "failed");

        store.Entries.Should().ContainSingle();
        store.Entries[0].Category.Should().Be("NoNamespace");
        store.Entries[0].Level.Should().Be(nameof(GatewayLogLevel.Error));
        store.Entries[0].Detail.Should().Contain("boom");
        store.Entries[0].Message.Should().Be("failed");
    }

    [Fact]
    public void Log_Critical_MapsToCriticalLevel()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("Cat");

        logger.LogCritical("down");

        store.Entries[0].Level.Should().Be(nameof(GatewayLogLevel.Critical));
    }

    [Fact]
    public void Log_WhenDisabled_DoesNotRecord()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("Cat");

        logger.LogInformation("ignored");

        store.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Log_StoreThrows_DoesNotPropagate()
    {
        using var provider = new GatewayLogSinkProvider(() => throw new InvalidOperationException("store down"));
        var logger = provider.CreateLogger("Cat");

        var act = () => logger.LogError("still must not throw");

        act.Should().NotThrow();
    }

    [Fact]
    public void BeginScope_ReturnsDisposableScope()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("Cat");

        // Previously null, which is why the Logs tab's Request ID column was permanently empty:
        // without a scope stack the sink has no way to learn which request a log belongs to.
        logger.BeginScope("state").Should().NotBeNull();
    }

    [Fact]
    public void Log_TakesRequestIdFromAnEnclosingScope()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("Cat");

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            [GatewayLogScopeKeys.RequestId] = "req_abc",
        }))
        {
            logger.LogWarning("something went wrong");
        }

        store.Entries.Should().ContainSingle()
            .Which.RequestId.Should().Be("req_abc");
    }

    [Fact]
    public void Log_TakesModelIdFromStructuredState()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("Cat");

        // Structured properties on the log statement itself count, so existing call sites that
        // already write {ModelId} are picked up with no change.
        logger.LogWarning("upstream failed for {ModelId}", "gpt-4o");

        store.Entries.Should().ContainSingle()
            .Which.ModelId.Should().Be("gpt-4o");
    }

    [Fact]
    public void Log_LeavesScopeStackUnchangedAfterDispose()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("Cat");

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            [GatewayLogScopeKeys.RequestId] = "req_outer",
        }))
        {
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                [GatewayLogScopeKeys.RequestId] = "req_inner",
            }))
            {
                logger.LogWarning("inner");
            }

            logger.LogWarning("outer");
        }

        store.Entries.Select(e => e.RequestId).Should().Equal("req_inner", "req_outer");
    }

    [Fact]
    public void Log_AtErrorAndAbove_AlsoRecordsAnErrorRecord()
    {
        var store = new RecordingLogStore();
        var errors = new RecordingErrorRecorder();
        using var provider = new GatewayLogSinkProvider(
            () => store,
            () => errors,
            new GatewayErrorTrackingOptions());
        var logger = provider.CreateLogger("Cat");

        logger.LogWarning("a warning");
        logger.LogError(new InvalidOperationException("boom"), "an error");

        // A warning is a diagnostic; only an error is an error.
        errors.Records.Should().ContainSingle();
        errors.Records[0].Message.Should().Be("an error");
        errors.Records[0].ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        errors.Records[0].Source.Should().Be(GatewayErrorSourceNames.Log);
    }

    [Fact]
    public void Log_IgnoresDeniedCategories()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(
            () => store,
            null,
            new GatewayErrorTrackingOptions { IgnoredCategories = ["Microsoft.AspNetCore.Server.Kestrel"] });

        // Kestrel logs a warning for every client that drops a connection. Left in, those alone
        // would evict every real diagnostic from a 500-entry ring.
        provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel.Connections")
            .LogWarning("connection reset");

        store.Entries.Should().BeEmpty();
    }

    private sealed class RecordingErrorRecorder : IGatewayErrorRecorder
    {
        public List<GatewayErrorRecord> Records { get; } = [];

        public void Record(GatewayErrorRecord record) => Records.Add(record);
    }

    [Fact]
    public void Dispose_ClearsCachedLoggers()
    {
        var store = new RecordingLogStore();
        var provider = new GatewayLogSinkProvider(() => store);
        var before = provider.CreateLogger("Cat");
        provider.Dispose();
        var after = provider.CreateLogger("Cat");

        after.Should().NotBeSameAs(before);
    }

    private sealed class RecordingLogStore : IGatewayLogStore
    {
        public List<GatewayLogEntry> Entries { get; } = [];

        public int Capacity => 1000;

        public void Record(GatewayLogEntry entry) => Entries.Add(entry);

        public IReadOnlyList<GatewayLogEntry> GetRecent(
            int limit,
            GatewayLogLevel? minimumLevel = null,
            string? search = null) => Entries.Take(limit).ToList();

        public int Clear()
        {
            var removed = Entries.Count;
            Entries.Clear();
            return removed;
        }
    }
}
