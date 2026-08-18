using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <summary>
    /// Data repair for <c>20260719100201_DateTimeOffsetAsUtcTicks</c>. That migration switched every
    /// <c>DateTimeOffset</c> column from TEXT to INTEGER (UTC ticks) through an EF-SQLite table
    /// rebuild, which copies rows verbatim: ISO-8601 text such as <c>2026-07-19 05:31:30+00:00</c>
    /// does not convert under INTEGER affinity and stayed TEXT, so any database created before that
    /// migration and upgraded across it read those rows back as year-0001 timestamps (GetInt64 on
    /// the text yields its numeric prefix). This converts every remaining TEXT value in those columns
    /// to UTC ticks in place.
    /// </summary>
    /// <remarks>
    /// Idempotent: each UPDATE is guarded by <c>typeof(col) = 'text'</c>, so already-numeric rows —
    /// including databases created after the tick migration — are untouched and re-running is a
    /// no-op. Sub-millisecond precision of the original text value is not preserved (julianday is a
    /// double), which is far below the resolution anything reads these columns at.
    /// </remarks>
    public partial class DateTimeOffsetTextToUtcTicks : Migration
    {
        /// <summary>
        /// Every (table, column) that <c>20260719100201_DateTimeOffsetAsUtcTicks</c> altered.
        /// </summary>
        private static readonly (string Table, string Column)[] TickColumns =
        [
            ("tenants", "UpdatedAt"),
            ("tenants", "CreatedAt"),
            ("recent_requests", "TimestampUtc"),
            ("rate_limit_plans", "UpdatedAt"),
            ("rate_limit_defaults", "UpdatedAt"),
            ("rate_cards", "UpdatedAt"),
            ("rate_cards", "EffectiveUntil"),
            ("rate_cards", "EffectiveFrom"),
            ("rate_cards", "CreatedAt"),
            ("quota_usages", "UpdatedAt"),
            ("quota_usage_snapshots", "UpdatedAt"),
            ("quota_settings", "UpdatedAt"),
            ("quota_allocations", "UpdatedAt"),
            ("quota_allocations", "CreatedAt"),
            ("plans", "UpdatedAt"),
            ("plans", "CreatedAt"),
            ("model_routes", "UpdatedAt"),
            ("gateway_stats_snapshot", "UpdatedAt"),
            ("daily_usage_rollups", "UpdatedAt"),
            ("cors_settings", "UpdatedAt"),
            ("config_version", "UpdatedAt"),
            ("budgets", "UpdatedAt"),
            ("budgets", "CreatedAt"),
            ("billing_events", "RecordedAt"),
            ("api_keys", "RevokedAt"),
            ("api_keys", "LastUsedAt"),
            ("api_keys", "ExpiresAt"),
            ("api_keys", "CreatedAt"),
        ];

        /// <summary>
        /// SQL expression converting an ISO-8601 text value (any offset) to .NET UTC ticks:
        /// milliseconds since the Unix epoch, times 10 000 ticks/ms, plus the epoch's tick offset.
        /// julianday() honours the trailing "+HH:MM" offset, so the result is the true UTC instant.
        /// </summary>
        public static string ToUtcTicksSql(string column) =>
            $"CAST(ROUND((julianday(\"{column}\") - 2440587.5) * 86400000.0) AS INTEGER) * 10000 + 621355968000000000";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in TickColumns)
            {
                migrationBuilder.Sql(
                    $"UPDATE \"{table}\" SET \"{column}\" = {ToUtcTicksSql(column)} " +
                    $"WHERE typeof(\"{column}\") = 'text';");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the tick representation is what the schema declares, and the previous
            // migration's Down rebuilds the columns as TEXT on its own.
        }
    }
}
