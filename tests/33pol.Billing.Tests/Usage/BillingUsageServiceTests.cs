using Pol33.Billing.Usage;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingUsageServiceTests
{
    private readonly IDailyUsageRollupRepository _rollups = Substitute.For<IDailyUsageRollupRepository>();
    private readonly IBillingEventRepository _events = Substitute.For<IBillingEventRepository>();
    private readonly BillingUsageService _service;

    public BillingUsageServiceTests()
    {
        _service = new BillingUsageService(_rollups, _events);
    }

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
            .GetRollupsAsync(null, null, null, Arg.Any<CancellationToken>())
            .Returns(rollupRecords);

        var report = await _service.GetUsageReportAsync(new UsageReportRequest());

        report.Rollups.Should().BeEquivalentTo(rollupRecords);
        report.Summary.TotalPromptTokens.Should().Be(300);
        report.Summary.TotalCompletionTokens.Should().Be(150);
        report.Summary.TotalCost.Should().Be(0.40m);
        report.Summary.TotalRequests.Should().Be(5);
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
            .GetRollupsAsync(from, to, tenantId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DailyUsageRollupRecord>());

        await _service.GetUsageReportAsync(request);

        await _rollups
            .Received(1)
            .GetRollupsAsync(from, to, tenantId, Arg.Any<CancellationToken>());
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
    public async Task QueryEventsAsync_DelegatesToRepository()
    {
        var query = new BillingEventQuery(
            new DateOnly(2026, 5, 1),
            new DateOnly(2026, 5, 31),
            Guid.NewGuid(),
            50);

        _events.QueryAsync(query, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<BillingEventRecord>());

        var page = await _service.QueryEventsAsync(query);

        page.Limit.Should().Be(50);
        page.Events.Should().BeEmpty();
        await _events.Received(1).QueryAsync(query, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryEventsAsync_ClampsLimitToMaximum()
    {
        _events.QueryAsync(Arg.Any<BillingEventQuery>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<BillingEventRecord>());

        var page = await _service.QueryEventsAsync(new BillingEventQuery(null, null, null, 9999));

        page.Limit.Should().Be(5000);
        await _events.Received(1).QueryAsync(
            Arg.Is<BillingEventQuery>(q => q.Limit == 5000),
            Arg.Any<CancellationToken>());
    }
}
