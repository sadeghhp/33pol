using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
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
    public void BeginScope_ReturnsNull()
    {
        var store = new RecordingLogStore();
        using var provider = new GatewayLogSinkProvider(() => store);
        var logger = provider.CreateLogger("Cat");

        logger.BeginScope("state").Should().BeNull();
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

        public void Clear() => Entries.Clear();
    }
}
