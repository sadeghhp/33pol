using Pol33.Core.Billing;

namespace Pol33.Billing.Tests.Aggregates;

public sealed class DailyUsageRollupAggregatorTests
{
    private readonly DailyUsageRollupAggregator _aggregator = new();

    [Fact]
    public void Aggregate_SameTenantModelDay_SumsTokensAndCost()
    {
        var tenantId = Guid.NewGuid();
        var day = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

        var events = new[]
        {
            CreateEvent(tenantId, "gpt-4o", day, prompt: 100, completion: 50, cost: 0.10m),
            CreateEvent(tenantId, "gpt-4o", day.AddHours(2), prompt: 200, completion: 100, cost: 0.20m),
        };

        var rollups = _aggregator.Aggregate(events);

        rollups.Should().ContainSingle();
        rollups[0].PromptTokens.Should().Be(300);
        rollups[0].CompletionTokens.Should().Be(150);
        rollups[0].TotalCost.Should().Be(0.30m);
        rollups[0].RequestCount.Should().Be(2);
    }

    [Fact]
    public void Aggregate_DifferentModels_ProducesSeparateRollups()
    {
        var tenantId = Guid.NewGuid();
        var day = new DateTimeOffset(2026, 5, 26, 8, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            CreateEvent(tenantId, "gpt-4o", day, 10, 5, 0.01m),
            CreateEvent(tenantId, "gpt-4o-mini", day, 20, 10, 0.02m),
        };

        var rollups = _aggregator.Aggregate(events);

        rollups.Should().HaveCount(2);
        rollups.Select(r => r.ModelId).Should().BeEquivalentTo(["gpt-4o", "gpt-4o-mini"]);
    }

    [Fact]
    public void Aggregate_DifferentDays_ProducesSeparateRollups()
    {
        var tenantId = Guid.NewGuid();
        var events = new[]
        {
            CreateEvent(tenantId, "gpt-4o", new DateTimeOffset(2026, 5, 26, 23, 0, 0, TimeSpan.Zero), 1, 1, 0.01m),
            CreateEvent(tenantId, "gpt-4o", new DateTimeOffset(2026, 5, 27, 1, 0, 0, TimeSpan.Zero), 2, 2, 0.02m),
        };

        var rollups = _aggregator.Aggregate(events);

        rollups.Should().HaveCount(2);
        rollups.Select(r => r.UsageDate).Should().BeEquivalentTo([
            new DateOnly(2026, 5, 26),
            new DateOnly(2026, 5, 27),
        ]);
    }

    [Fact]
    public void Aggregate_DifferentCostCenters_SplitsRollups()
    {
        var tenantId = Guid.NewGuid();
        var day = new DateTimeOffset(2026, 5, 26, 10, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            CreateEvent(tenantId, "gpt-4o", day, 10, 5, 0.01m, "eng"),
            CreateEvent(tenantId, "gpt-4o", day, 20, 10, 0.02m, "research"),
        };

        var rollups = _aggregator.Aggregate(events);

        rollups.Should().HaveCount(2);
        rollups.Select(r => r.CostCenter).Should().BeEquivalentTo(["eng", "research"]);
    }

    [Fact]
    public void Aggregate_EmptyInput_ReturnsEmpty()
    {
        _aggregator.Aggregate(Array.Empty<BillingEventRecord>()).Should().BeEmpty();
    }

    private static BillingEventRecord CreateEvent(
        Guid tenantId,
        string modelId,
        DateTimeOffset recordedAt,
        long prompt,
        long completion,
        decimal cost,
        string? costCenter = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            tenantId,
            Guid.NewGuid(),
            modelId,
            costCenter,
            prompt,
            completion,
            cost,
            cost,
            cost,
            10,
            recordedAt);
}
