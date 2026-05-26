using System.Globalization;
using System.Text;
using System.Text.Json;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public static class UsageExportFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static UsageExportResult Format(
        IReadOnlyList<DailyUsageRollupRecord> rollups,
        string format)
    {
        ArgumentNullException.ThrowIfNull(rollups);

        return format.Trim().ToLowerInvariant() switch
        {
            "csv" => new UsageExportResult
            {
                ContentType = "text/csv",
                FileName = $"usage-export-{DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}.csv",
                Body = ToCsv(rollups),
            },
            _ => new UsageExportResult
            {
                ContentType = "application/json",
                FileName = $"usage-export-{DateOnly.FromDateTime(DateTime.UtcNow):yyyy-MM-dd}.json",
                Body = JsonSerializer.Serialize(rollups, JsonOptions),
            },
        };
    }

    private static string ToCsv(IReadOnlyList<DailyUsageRollupRecord> rollups)
    {
        var builder = new StringBuilder();
        builder.AppendLine("usage_date,tenant_id,model_id,cost_center,prompt_tokens,completion_tokens,total_cost,request_count");

        foreach (var rollup in rollups)
        {
            builder.Append(rollup.UsageDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(rollup.TenantId?.ToString() ?? string.Empty);
            builder.Append(',');
            builder.Append(EscapeCsv(rollup.ModelId));
            builder.Append(',');
            builder.Append(EscapeCsv(rollup.CostCenter ?? string.Empty));
            builder.Append(',');
            builder.Append(rollup.PromptTokens.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(rollup.CompletionTokens.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(rollup.TotalCost.ToString(CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.AppendLine(rollup.RequestCount.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"', StringComparison.Ordinal) || value.Contains(',', StringComparison.Ordinal))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}
