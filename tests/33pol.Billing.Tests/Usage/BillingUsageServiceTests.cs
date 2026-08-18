using Microsoft.Extensions.Options;
using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;
using Pol33.Core.Identity;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingUsageServiceTests
{
    private readonly IDailyUsageRollupRepository _rollups = Substitute.For<IDailyUsageRollupRepository>();
    private readonly IBillingEventRepository _events = Substitute.For<IBillingEventRepository>();
    private readonly IApiKeyRepository _apiKeys = Substitute.For<IApiKeyRepository>();
    private readonly IRateCardRepository _rateCards = Substitute.For<IRateCardRepository>();
    private readonly IModelRegistry _registry = Substitute.For<IModelRegistry>();
    private readonly BillingUsageService _service;

    public BillingUsageServiceTests()
    {
        _rateCards.GetActiveByModelAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, RateCardRecord>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-4o"] = RateCard("gpt-4o"),
                ["gpt-4o-mini"] = RateCard("gpt-4o-mini"),
            });
        _apiKeys.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApiKeyRecord>());
        // Every model id is "registered" unless a test says otherwise; retired models are the
        // exception exercised explicitly.
        _registry.TryGetModel(Arg.Any<string>(), out Arg.Any<ModelConfig?>()).Returns(true);
        _service = new BillingUsageService(
            _rollups, _events, _apiKeys, _rateCards, Options.Create(new BillingOptions { DefaultCurrency = "EUR" }), _registry);
    }

    private static RateCardRecord RateCard(string modelId) => new(
        Guid.NewGuid(), modelId, modelId, modelId, 1m, 2m, "USD",
        DateTimeOffset.UnixEpoch, null, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task GetUsageReportAsync_WithRollups_ComputesSummaryTotals()
    {
        var tenantId = Guid.NewGuid();
        var rollupRecords = new[]
        {
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 26),
                tenantId,
                "gpt-4o",
                "eng",
                100,
                50,
                0.15m,
                2),
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 27),
                tenantId,
                "gpt-4o-mini",
                "eng",
                200,
                100,
                0.25m,
                3),
        };

        _rollups
            .GetScopedRollupsAsync(Arg.Any<UsageScope>(), null, null, Arg.Any<CancellationToken>())
            .Returns(rollupRecords);

        var report = await _service.GetUsageReportAsync(new UsageReportRequest());

        report.Rollups.Should().BeEquivalentTo(rollupRecords);
        report.Summary.TotalPromptTokens.Should().Be(300);
        report.Summary.TotalCompletionTokens.Should().Be(150);
        report.Summary.TotalCost.Should().Be(0.40m);
        report.Summary.TotalRequests.Should().Be(5);
        report.Summary.AnonymousRequests.Should().Be(0);
        report.Currency.Should().Be("EUR");
        report.Source.Should().Be(UsageReportSource.Rollups);
        report.UnpricedModelIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUsageReportAsync_ReportsUnpricedModelsAndAnonymousRequests()
    {
        var tenantId = Guid.NewGuid();
        var rollupRecords = new[]
        {
            new DailyUsageRollupRecord(new DateOnly(2026, 5, 26), tenantId, "gpt-4o", "eng", 100, 50, 0.15m, 2),
            new DailyUsageRollupRecord(new DateOnly(2026, 5, 26), null, "Free-Model", null, 10, 5, 0m, 4),
        };

        _rollups
            .GetScopedRollupsAsync(
                Arg.Is<UsageScope>(s => s.TenantId == tenantId && s.IncludeAnonymous),
                null,
                null,
                Arg.Any<CancellationToken>())
            .Returns(rollupRecords);

        var report = await _service.GetUsageReportAsync(
            new UsageReportRequest { TenantId = tenantId, IncludeAnonymous = true });

        report.Summary.TotalRequests.Should().Be(6);
        report.Summary.AnonymousRequests.Should().Be(4);
        report.UnpricedModelIds.Should().Equal("Free-Model");
    }

    /// <summary>
    /// Rollups outlive models. A model deleted long ago still has rows in the range and no rate
    /// card, but it must not be reported as unpriced: there is nothing to price any more and the
    /// "set pricing under Routing → Models" hint would point at a model that does not exist.
    /// </summary>
    [Fact]
    public async Task GetUsageReportAsync_RetiredModelWithoutRateCard_IsNotReportedAsUnpriced()
    {
        var tenantId = Guid.NewGuid();
        _registry.TryGetModel("retired-embed", out Arg.Any<ModelConfig?>()).Returns(false);
        _rollups
            .GetScopedRollupsAsync(Arg.Any<UsageScope>(), null, null, Arg.Any<CancellationToken>())
            .Returns(
            [
                new DailyUsageRollupRecord(new DateOnly(2026, 8, 1), tenantId, "retired-embed", null, 10, 0, 0m, 3),
                new DailyUsageRollupRecord(new DateOnly(2026, 8, 1), tenantId, "Free-Model", null, 10, 5, 0m, 1),
                new DailyUsageRollupRecord(new DateOnly(2026, 8, 1), tenantId, "gpt-4o", null, 10, 5, 1m, 1),
            ]);

        var report = await _service.GetUsageReportAsync(new UsageReportRequest { TenantId = tenantId });

        report.UnpricedModelIds.Should().Equal("Free-Model");
        report.Summary.TotalRequests.Should().Be(5, "retired usage still counts in the totals");
    }

    [Fact]
    public async Task GetUsageReportAsync_WithApiKey_AggregatesFromLedger()
    {
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var from = new DateOnly(2026, 5, 1);
        var to = new DateOnly(2026, 5, 31);
        var aggregated = new[]
        {
            new DailyUsageRollupRecord(from, tenantId, "gpt-4o", "eng", 10, 5, 0.01m, 1),
        };

        _events
            .AggregateDailyAsync(
                Arg.Is<BillingEventQuery>(q =>
                    q.FromDate == from && q.ToDate == to && q.TenantId == tenantId && q.ApiKeyId == keyId &&
                    q.CostCenter == "eng"),
                Arg.Any<CancellationToken>())
            .Returns(aggregated);

        var report = await _service.GetUsageReportAsync(new UsageReportRequest
        {
            FromDate = from,
            ToDate = to,
            TenantId = tenantId,
            ApiKeyId = keyId,
            CostCenter = "eng",
        });

        report.Source.Should().Be(UsageReportSource.Events);
        report.Rollups.Should().BeEquivalentTo(aggregated);
        await _rollups.DidNotReceiveWithAnyArgs().GetScopedRollupsAsync(default!, default, default, default);
    }

    [Fact]
    public async Task GetUsageReportAsync_PassesFiltersToRepository()
    {
        var tenantId = Guid.NewGuid();
        var from = new DateOnly(2026, 5, 1);
        var to = new DateOnly(2026, 5, 31);
        var request = new UsageReportRequest
        {
            FromDate = from,
            ToDate = to,
            TenantId = tenantId,
        };

        _rollups
            .GetScopedRollupsAsync(Arg.Any<UsageScope>(), from, to, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        await _service.GetUsageReportAsync(request);

        await _rollups
            .Received(1)
            .GetScopedRollupsAsync(
                Arg.Is<UsageScope>(s => s.TenantId == tenantId && !s.IncludeAnonymous),
                from,
                to,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ExportRollups_DelegatesToFormatter()
    {
        var rollups = new[]
        {
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 26),
                Guid.NewGuid(),
                "gpt-4o",
                null,
                10,
                5,
                0.01m,
                1),
        };

        var result = _service.ExportRollups(rollups, "csv");

        result.ContentType.Should().Be("text/csv");
        result.Body.Should().Contain("gpt-4o");
        result.FileName.Should().StartWith("usage-export-").And.EndWith(".csv");
    }

    [Fact]
    public async Task GetUsageReportAsync_FiltersByCostCenter()
    {
        var tenantId = Guid.NewGuid();
        var rollupRecords = new[]
        {
            new DailyUsageRollupRecord(new DateOnly(2026, 5, 26), tenantId, "gpt-4o", "eng", 100, 50, 0.15m, 2),
            new DailyUsageRollupRecord(new DateOnly(2026, 5, 26), tenantId, "gpt-4o", "ops", 200, 100, 0.25m, 3),
        };

        _rollups
            .GetScopedRollupsAsync(Arg.Any<UsageScope>(), null, null, Arg.Any<CancellationToken>())
            .Returns(rollupRecords);

        var report = await _service.GetUsageReportAsync(new UsageReportRequest { CostCenter = "ENG" });

        report.Rollups.Should().ContainSingle();
        report.Rollups[0].CostCenter.Should().Be("eng");
        report.Summary.TotalRequests.Should().Be(2);
    }

    [Fact]
    public async Task GetUsageReportAsync_NoCostCenter_SelectsRowsWithoutOne()
    {
        var tenantId = Guid.NewGuid();
        var rollupRecords = new[]
        {
            new DailyUsageRollupRecord(new DateOnly(2026, 5, 26), tenantId, "gpt-4o", "eng", 100, 50, 0.15m, 2),
            new DailyUsageRollupRecord(new DateOnly(2026, 5, 26), tenantId, "gpt-4o", null, 200, 100, 0.25m, 3),
        };

        _rollups
            .GetScopedRollupsAsync(Arg.Any<UsageScope>(), null, null, Arg.Any<CancellationToken>())
            .Returns(rollupRecords);

        var report = await _service.GetUsageReportAsync(
            new UsageReportRequest { CostCenter = "eng", NoCostCenter = true });

        report.Rollups.Should().ContainSingle();
        report.Rollups[0].CostCenter.Should().BeNull();
    }

    [Fact]
    public async Task QueryEventsAsync_DelegatesToRepository()
    {
        var query = new BillingEventQuery(
            new DateOnly(2026, 5, 1),
            new DateOnly(2026, 5, 31),
            Guid.NewGuid(),
            Limit: 50);

        _events.QueryAsync(Arg.Any<BillingEventQuery>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<BillingEventRecord>());

        var page = await _service.QueryEventsAsync(query);

        page.Limit.Should().Be(50);
        page.Events.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.NextCursor.Should().BeNull();
        // One extra row is requested so HasMore is exact without a count query.
        await _events.Received(1).QueryAsync(query with { Limit = 51 }, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEventsAsync_WhenMoreRowsExist_TrimsPageAndIssuesCursor()
    {
        var tenantId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
        BillingEventRecord Row(int i, DateTimeOffset when) => new(
            Guid.NewGuid(), "req-" + i, tenantId, null, "gpt-4o", null, 1, 1, null, null, 0.001m, 10, when);
        var rows = new[] { Row(1, at.AddSeconds(3)), Row(2, at.AddSeconds(2)), Row(3, at.AddSeconds(2)), Row(4, at) };

        _events.QueryAsync(Arg.Any<BillingEventQuery>(), Arg.Any<CancellationToken>()).Returns(rows);

        var page = await _service.QueryEventsAsync(new BillingEventQuery(TenantId: tenantId, Limit: 3));

        page.Events.Should().HaveCount(3);
        page.HasMore.Should().BeTrue();
        BillingEventCursor.TryDecode(page.NextCursor, out var cursor).Should().BeTrue();
        cursor!.At.Should().Be(at.AddSeconds(2));
        cursor.BoundaryIds.Should().BeEquivalentTo(new[] { rows[1].Id, rows[2].Id });
    }

    [Fact]
    public async Task QueryEventsAsync_ClampsLimitToMaximum()
    {
        _events.QueryAsync(Arg.Any<BillingEventQuery>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<BillingEventRecord>());

        var page = await _service.QueryEventsAsync(new BillingEventQuery(Limit: 9999));

        page.Limit.Should().Be(5000);
        await _events.Received(1).QueryAsync(
            Arg.Is<BillingEventQuery>(q => q.Limit == 5001),
            Arg.Any<CancellationToken>());
    }
}
