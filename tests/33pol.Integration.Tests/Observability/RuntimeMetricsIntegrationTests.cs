using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Observability;

/// <summary>
/// The .NET runtime series must stay on the scrape endpoint.
/// </summary>
/// <remarks>
/// <para>The gateway's dominant failure mode is memory, not request rate: a request body is
/// buffered, scanned and forwarded, so heap pressure is what decides whether the process stays
/// inside its container limit. The ASP.NET Core and HttpClient instrumentations cover request and
/// dependency traffic and say nothing about any of that, so without runtime instrumentation an
/// OOMKill, a GC-bound tail latency, or Large Object Heap growth under long-context traffic are all
/// invisible in production.</para>
///
/// <para>Each name below is referenced by a runbook or a capacity check, so this test exists to make
/// removing the instrumentation — or a package upgrade renaming the series — a build failure rather
/// than a silently blank dashboard. The names follow the current OpenTelemetry semantic conventions
/// (<c>dotnet_*</c>), not the legacy <c>process_runtime_dotnet_*</c> ones.</para>
/// </remarks>
public sealed class RuntimeMetricsIntegrationTests
{
    [Theory]
    // Resident memory: the number to compare against the container limit.
    [InlineData("dotnet_process_memory_working_set_bytes")]
    // Cumulative allocation: what shows whether the request path allocates in proportion to body size.
    [InlineData("dotnet_gc_heap_total_allocated_bytes_total")]
    // Per-generation heap size, including the loh dimension asserted separately below.
    [InlineData("dotnet_gc_last_collection_heap_size_bytes")]
    [InlineData("dotnet_gc_last_collection_memory_committed_size_bytes")]
    // Collection counts and pause time: the link between memory pressure and tail latency.
    [InlineData("dotnet_gc_collections_total")]
    [InlineData("dotnet_gc_pause_time_seconds_total")]
    // Saturation signals that precede a stall under concurrent large-body work.
    [InlineData("dotnet_thread_pool_queue_length_total")]
    [InlineData("dotnet_monitor_lock_contentions_total")]
    public async Task MetricsEndpoint_ExposesRuntimeSeries(string metricName)
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var metrics = await client.GetStringAsync("/metrics");

        metrics.Should().Contain(metricName);
    }

    /// <summary>
    /// The Large Object Heap dimension specifically: every buffer above 85 KB lands there, which is
    /// the regime a multi-megabyte request body operates in. Watching only total heap size hides it.
    /// </summary>
    [Fact]
    public async Task MetricsEndpoint_ExposesLargeObjectHeapDimension()
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var metrics = await client.GetStringAsync("/metrics");

        metrics.Should().Contain("gc_heap_generation=\"loh\"");
        metrics.Should().Contain("gc_heap_generation=\"gen2\"");
    }
}
