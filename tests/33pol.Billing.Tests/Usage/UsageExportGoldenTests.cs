using Pol33.Billing.Usage;
using Pol33.Core.Billing;

namespace Pol33.Billing.Tests.Usage;

public sealed class UsageExportGoldenTests
{
    private static readonly Guid GoldenTenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void Format_Csv_EmptyRollups_MatchesGoldenHeader()
    {
        var expected = ReadGolden("usage-export-empty.csv");
        var result = UsageExportFormatter.Format(Array.Empty<DailyUsageRollupRecord>(), "csv");

        NormalizeLineEndings(result.Body).Should().Be(NormalizeLineEndings(expected));
    }

    [Fact]
    public void Format_Csv_SampleRollup_MatchesGoldenBody()
    {
        var expected = ReadGolden("usage-export-sample.csv");
        var rollups = new[]
        {
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 26),
                GoldenTenantId,
                "gpt-4o",
                "eng",
                100,
                50,
                0.15m,
                2),
        };

        var result = UsageExportFormatter.Format(rollups, "csv");

        NormalizeLineEndings(result.Body).Should().Be(NormalizeLineEndings(expected));
    }

    [Fact]
    public void Format_Json_SampleRollup_MatchesGoldenStructure()
    {
        var rollups = new[]
        {
            new DailyUsageRollupRecord(
                new DateOnly(2026, 5, 26),
                GoldenTenantId,
                "gpt-4o",
                "eng",
                100,
                50,
                0.15m,
                2),
        };

        var result = UsageExportFormatter.Format(rollups, "json");

        using var actual = System.Text.Json.JsonDocument.Parse(result.Body);
        using var expected = System.Text.Json.JsonDocument.Parse(ReadGolden("usage-export-sample.json"));

        actual.RootElement.GetArrayLength().Should().Be(1);
        var row = actual.RootElement[0];
        row.GetProperty("modelId").GetString().Should().Be("gpt-4o");
        row.GetProperty("totalCost").GetDecimal().Should().Be(0.15m);
        expected.RootElement.GetArrayLength().Should().Be(1);
    }

    private static string ReadGolden(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        if (!File.Exists(path))
        {
            path = Path.Combine(GetRepoTestDataPath(), fileName);
        }

        return File.ReadAllText(path);
    }

    private static string GetRepoTestDataPath() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "TestData"));

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
}
