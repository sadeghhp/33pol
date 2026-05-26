using Pol33.Billing.Usage;
using Pol33.Core.Billing;

namespace Pol33.Billing.Tests.Usage;

public sealed class UsageExportFormatterTests
{
    [Fact]
    public void Format_Csv_IncludesHeaderAndRow()
    {
        var rollups = new[]
        {
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 26),
                Guid.NewGuid(),
                "gpt-4o",
                "eng",
                100,
                50,
                0.15m,
                2),
        };

        var result = UsageExportFormatter.Format(rollups, "csv");

        result.ContentType.Should().Be("text/csv");
        result.Body.Should().Contain("usage_date,tenant_id");
        result.Body.Should().Contain("gpt-4o");
        result.Body.Should().Contain("eng");
    }

    [Fact]
    public void Format_Json_ReturnsJsonArray()
    {
        var rollups = new[]
        {
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 26),
                null,
                "m1",
                null,
                1,
                1,
                0m,
                1),
        };

        var result = UsageExportFormatter.Format(rollups, "json");

        result.ContentType.Should().Be("application/json");
        result.Body.Should().Contain("\"modelId\": \"m1\"");
    }

    [Fact]
    public void Format_Csv_EscapesSpecialCharacters()
    {
        var rollups = new[]
        {
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 26),
                Guid.NewGuid(),
                "model,with,comma",
                "cost\"center",
                1,
                1,
                0m,
                1),
        };

        var result = UsageExportFormatter.Format(rollups, "csv");

        result.Body.Should().Contain("\"model,with,comma\"");
        result.Body.Should().Contain("\"cost\"\"center\"");
    }

    [Fact]
    public void Format_UnknownFormat_DefaultsToJson()
    {
        var rollups = new[]
        {
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 26),
                null,
                "m1",
                null,
                1,
                1,
                0m,
                1),
        };

        var result = UsageExportFormatter.Format(rollups, "xml");

        result.ContentType.Should().Be("application/json");
    }
}
