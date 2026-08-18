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

    /// <summary>Ledger export. Timestamps are UTC ISO-8601; costs are raw decimals (no rounding).</summary>
    public static UsageExportResult FormatEvents(
        IReadOnlyList<AdminBillingEventListItem> events,
        string format,
        DateOnly? from,
        DateOnly? to,
        bool truncated)
    {
        ArgumentNullException.ThrowIfNull(events);

        var stamp = RangeStamp(from, to);
        return (format ?? "json").Trim().ToLowerInvariant() switch
        {
            "csv" => new UsageExportResult
            {
                ContentType = "text/csv",
                FileName = $"usage-events-{stamp}.csv",
                Body = EventsToCsv(events),
                Truncated = truncated,
            },
            _ => new UsageExportResult
            {
                ContentType = "application/json",
                FileName = $"usage-events-{stamp}.json",
                Body = JsonSerializer.Serialize(events, JsonOptions),
                Truncated = truncated,
            },
        };
    }

    private static string RangeStamp(DateOnly? from, DateOnly? to)
    {
        var f = from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "start";
        var t = to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                ?? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return f + "_" + t;
    }

    private static string EventsToCsv(IReadOnlyList<AdminBillingEventListItem> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("recorded_at_utc,request_id,api_key_id,key_prefix,assignee,model_id,cost_center,prompt_tokens,completion_tokens,total_cost,duration_ms");

        foreach (var e in events)
        {
            builder.Append(e.RecordedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append(',');
            builder.Append(EscapeCsv(e.RequestId)).Append(',');
            builder.Append(e.ApiKeyId?.ToString() ?? string.Empty).Append(',');
            builder.Append(EscapeCsv(e.KeyPrefix ?? string.Empty)).Append(',');
            builder.Append(EscapeCsv(e.Assignee ?? string.Empty)).Append(',');
            builder.Append(EscapeCsv(e.ModelId)).Append(',');
            builder.Append(EscapeCsv(e.CostCenter ?? string.Empty)).Append(',');
            builder.Append(e.PromptTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(e.CompletionTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
            builder.Append(e.TotalCost?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',');
            builder.AppendLine(e.DurationMs.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
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
        if (value.Contains('"', StringComparison.Ordinal) || value.Contains(',', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}
