using Pol33.Core.Models;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.Runtime;

/// <summary>
/// Pricing arrives one flush interval after the row it belongs to was written, so the feed has to
/// join the two on read — whichever order they land in.
/// </summary>
public sealed class GatewayRuntimeStateUsageTests
{
    private static RecentRequestEntry Entry(string id, bool inFlight = false) => new()
    {
        RequestId = id,
        Method = "POST",
        Path = "/v1/chat/completions",
        ModelId = "m1",
        StatusCode = inFlight ? 0 : 200,
        TimestampUtc = DateTimeOffset.UtcNow,
        IsInFlight = inFlight,
        PricingStatus = inFlight ? null : RecentRequestUsage.StatusPending,
    };

    private static readonly RecentRequestUsage Priced = new(
        PromptTokens: 100,
        CompletionTokens: 50,
        TotalTokens: 150,
        TokenSource: "split",
        InputCost: 0.0003m,
        OutputCost: 0.00075m,
        TotalCost: 0.00105m,
        Currency: "USD",
        PricingStatus: RecentRequestUsage.StatusPriced);

    [Fact]
    public void AttachUsage_AfterCompletion_MergesCostsOntoTheRow()
    {
        var runtime = new GatewayRuntimeState();
        runtime.EnqueueRecent(Entry("r1"));

        runtime.AttachUsage("r1", Priced);

        var row = runtime.GetRecent(1).Single();
        row.PricingStatus.Should().Be("priced");
        row.InputCost.Should().Be(0.0003m);
        row.OutputCost.Should().Be(0.00075m);
        row.TotalCost.Should().Be(0.00105m);
        row.Currency.Should().Be("USD");
        row.PromptTokens.Should().Be(100);
        row.CompletionTokens.Should().Be(50);
    }

    [Fact]
    public void AttachUsage_BeforeCompletion_IsPickedUpWhenTheRowLands()
    {
        var runtime = new GatewayRuntimeState();
        runtime.BeginInFlight(Entry("r1", inFlight: true));

        // The writer flushed before the router recorded completion.
        runtime.AttachUsage("r1", Priced);
        runtime.GetRecent(1).Single().TotalCost.Should().Be(0.00105m, "the in-flight row already shows it");

        runtime.EnqueueRecent(Entry("r1"));

        var row = runtime.GetRecent(1).Single();
        row.IsInFlight.Should().BeFalse();
        row.PricingStatus.Should().Be("priced");
        row.TotalCost.Should().Be(0.00105m);
    }

    [Fact]
    public void AttachUsage_ForAnUnknownRequest_IsRetainedUntilTheRowArrives()
    {
        var runtime = new GatewayRuntimeState();
        runtime.AttachUsage("r-late", Priced);
        runtime.EnqueueRecent(Entry("r-late"));

        runtime.GetRecent(1).Single().PricingStatus.Should().Be("priced");
    }

    [Fact]
    public void Export_CarriesPricingSoARestartDoesNotResetRowsToPending()
    {
        var runtime = new GatewayRuntimeState();
        runtime.EnqueueRecent(Entry("r1"));
        runtime.AttachUsage("r1", Priced);

        var snapshot = runtime.Export();
        snapshot.Recent.Single().TotalCost.Should().Be(0.00105m);

        var restored = new GatewayRuntimeState();
        restored.Hydrate(snapshot);
        restored.GetRecent(1).Single().PricingStatus.Should().Be("priced");
    }

    [Fact]
    public void EvictedRows_DropTheirUsageToo()
    {
        var runtime = new GatewayRuntimeState { MaxRecentRequests = 1, MaxInFlightTracked = 1 };
        runtime.EnqueueRecent(Entry("r1"));
        runtime.AttachUsage("r1", Priced);
        runtime.EnqueueRecent(Entry("r2"));

        // r1 is gone from the feed; a row that later reuses the id starts clean.
        runtime.EnqueueRecent(Entry("r1"));
        runtime.GetRecent(1).Single().PricingStatus.Should().Be("pending");
    }

    [Fact]
    public void ResetAll_ClearsAttachedUsage()
    {
        var runtime = new GatewayRuntimeState();
        runtime.EnqueueRecent(Entry("r1"));
        runtime.AttachUsage("r1", Priced);

        runtime.ResetAll();
        runtime.EnqueueRecent(Entry("r1"));

        runtime.GetRecent(1).Single().PricingStatus.Should().Be("pending");
    }

    [Fact]
    public void Version_AdvancesOnEveryVisibleChange()
    {
        var runtime = new GatewayRuntimeState();
        var v0 = runtime.Version;

        runtime.RecordRequestStart("m1", isStreaming: false);
        var v1 = runtime.Version;
        runtime.BeginInFlight(Entry("r1", inFlight: true));
        var v2 = runtime.Version;
        runtime.EnqueueRecent(Entry("r1"));
        var v3 = runtime.Version;
        runtime.RecordRequestComplete("m1", success: true, durationMs: 10, wasStreaming: false);
        var v4 = runtime.Version;
        runtime.AttachUsage("r1", Priced);
        var v5 = runtime.Version;

        new[] { v0, v1, v2, v3, v4, v5 }.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Version_IsStableWhenNothingChanges()
    {
        var runtime = new GatewayRuntimeState();
        runtime.EnqueueRecent(Entry("r1"));
        var v = runtime.Version;

        runtime.GetRecent(5);
        runtime.GetStats();
        runtime.CompleteInFlight("nobody");

        runtime.Version.Should().Be(v);
    }

    [Fact]
    public void AttachUsage_FeedsTheWindowedTokensOnceAndCostOnlyWhenPriced()
    {
        var runtime = new GatewayRuntimeState();
        runtime.EnqueueRecent(Entry("r1"));

        runtime.AttachUsage("r1", Priced with { PricingStatus = RecentRequestUsage.StatusPending, TotalCost = null });
        runtime.AttachUsage("r1", Priced);

        var window = runtime.Windows.GetWindow(TimeSpan.FromMinutes(5));
        window.PromptTokens.Should().Be(100, "tokens are counted on the first attach only");
        window.CompletionTokens.Should().Be(50);
        window.PricedCost.Should().Be(0.00105m);
        window.PricedRequests.Should().Be(1);
        window.PerModel.Should().ContainSingle(m => m.ModelId == "m1" && m.PricedCost == 0.00105m);
    }
}
