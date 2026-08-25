using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pol33.App.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Observability.Runtime;

namespace Pol33.Integration.Tests.Admin;

public sealed class GatewayErrorAdminServiceTests
{
    /// <summary>
    /// A clear that cannot reach the database must still reset the counters, and must say that
    /// the rows are still there — not throw with the in-memory buffer already wiped.
    /// </summary>
    [Fact]
    public async Task ClearAllAsync_WhenTheArchiveDeleteFails_ResetsCountersAndReportsIt()
    {
        var store = Substitute.For<IGatewayErrorStore>();
        store.ClearAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database is locked"));

        var runtime = new GatewayRuntimeState();
        runtime.RecordRequestStart("m1", isStreaming: false);
        runtime.RecordRequestComplete("m1", success: false, durationMs: 10, wasStreaming: false);

        var services = new ServiceCollection().BuildServiceProvider();
        var admin = new GatewayErrorAdminService(
            store,
            runtime,
            new GatewayStatsFlushCoordinator(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GatewayErrorAdminService>.Instance);

        var result = await admin.ClearAllAsync(GatewayErrorClearScope.Errors);

        result.ArchiveCleared.Should().BeFalse();
        result.TotalErrorsCleared.Should().Be(1);
        runtime.GetStats().Errors.Should().Be(0);
        runtime.Windows.GetWindow(TimeSpan.FromMinutes(5), "5m").Errors.Should().Be(0);
    }
}
