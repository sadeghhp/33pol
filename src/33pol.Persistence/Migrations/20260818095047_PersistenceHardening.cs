using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <summary>
    /// Schema hardening:
    /// <list type="bullet">
    /// <item><c>daily_usage_rollups.TenantId</c> / <c>CostCenter</c> become NOT NULL with the sentinels
    /// <c>Guid.Empty</c> / <c>""</c> for anonymous traffic / no cost centre. SQLite treats NULLs as
    /// distinct in UNIQUE indexes, so those buckets (the common case) were not protected against
    /// duplicate rows; existing NULLs are rewritten to the sentinels and any duplicates that already
    /// slipped in are merged (summed) before the constraint tightens.</item>
    /// <item><c>model_routes.ModelId</c> gets the NOCASE collation, matching the registry's
    /// case-insensitive resolution; if a database already holds two routes differing only in case the
    /// most recently updated one is kept.</item>
    /// <item><c>billing_events.CostCenter</c> gets the NOCASE collation so cost-centre filters can use
    /// the (CostCenter, RecordedAt) index instead of wrapping the column in lower().</item>
    /// </list>
    /// </summary>
    public partial class PersistenceHardening : Migration
    {
        private const string AnonymousTenantId = "00000000-0000-0000-0000-000000000000";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- daily_usage_rollups: merge buckets the NULL-blind unique index let through, then
            // NULL -> sentinel. The merge runs first, with NULL-safe (IS) key comparison: once both
            // sentinels are in place two such rows would collide on the existing index. Only rows
            // that actually have a twin are touched, so on a healthy database this is a no-op.
            migrationBuilder.Sql(
                "UPDATE daily_usage_rollups AS keep SET " +
                "  \"PromptTokens\" = (SELECT SUM(d.\"PromptTokens\") FROM daily_usage_rollups d WHERE d.\"UsageDate\" = keep.\"UsageDate\" AND d.\"TenantId\" IS keep.\"TenantId\" AND d.\"ModelId\" = keep.\"ModelId\" AND d.\"CostCenter\" IS keep.\"CostCenter\"), " +
                "  \"CompletionTokens\" = (SELECT SUM(d.\"CompletionTokens\") FROM daily_usage_rollups d WHERE d.\"UsageDate\" = keep.\"UsageDate\" AND d.\"TenantId\" IS keep.\"TenantId\" AND d.\"ModelId\" = keep.\"ModelId\" AND d.\"CostCenter\" IS keep.\"CostCenter\"), " +
                "  \"TotalCost\" = (SELECT SUM(d.\"TotalCost\") FROM daily_usage_rollups d WHERE d.\"UsageDate\" = keep.\"UsageDate\" AND d.\"TenantId\" IS keep.\"TenantId\" AND d.\"ModelId\" = keep.\"ModelId\" AND d.\"CostCenter\" IS keep.\"CostCenter\"), " +
                "  \"RequestCount\" = (SELECT SUM(d.\"RequestCount\") FROM daily_usage_rollups d WHERE d.\"UsageDate\" = keep.\"UsageDate\" AND d.\"TenantId\" IS keep.\"TenantId\" AND d.\"ModelId\" = keep.\"ModelId\" AND d.\"CostCenter\" IS keep.\"CostCenter\"), " +
                "  \"UpdatedAt\" = (SELECT MAX(d.\"UpdatedAt\") FROM daily_usage_rollups d WHERE d.\"UsageDate\" = keep.\"UsageDate\" AND d.\"TenantId\" IS keep.\"TenantId\" AND d.\"ModelId\" = keep.\"ModelId\" AND d.\"CostCenter\" IS keep.\"CostCenter\") " +
                "WHERE keep.\"Id\" = (SELECT MIN(d.\"Id\") FROM daily_usage_rollups d WHERE d.\"UsageDate\" = keep.\"UsageDate\" AND d.\"TenantId\" IS keep.\"TenantId\" AND d.\"ModelId\" = keep.\"ModelId\" AND d.\"CostCenter\" IS keep.\"CostCenter\") " +
                "  AND EXISTS (SELECT 1 FROM daily_usage_rollups d WHERE d.\"UsageDate\" = keep.\"UsageDate\" AND d.\"TenantId\" IS keep.\"TenantId\" AND d.\"ModelId\" = keep.\"ModelId\" AND d.\"CostCenter\" IS keep.\"CostCenter\" AND d.\"Id\" <> keep.\"Id\");");
            migrationBuilder.Sql(
                "DELETE FROM daily_usage_rollups WHERE \"Id\" <> (SELECT MIN(d.\"Id\") FROM daily_usage_rollups d " +
                "WHERE d.\"UsageDate\" = daily_usage_rollups.\"UsageDate\" AND d.\"TenantId\" IS daily_usage_rollups.\"TenantId\" AND d.\"ModelId\" = daily_usage_rollups.\"ModelId\" AND d.\"CostCenter\" IS daily_usage_rollups.\"CostCenter\");");

            migrationBuilder.Sql(
                $"UPDATE daily_usage_rollups SET \"TenantId\" = '{AnonymousTenantId}' WHERE \"TenantId\" IS NULL;");
            migrationBuilder.Sql(
                "UPDATE daily_usage_rollups SET \"CostCenter\" = '' WHERE \"CostCenter\" IS NULL;");

            // --- model_routes: drop case-only duplicates (keep the most recently updated; SQLite's
            // bare-column-with-MAX picks that row) so the NOCASE unique index can be built ---
            migrationBuilder.Sql(
                "DELETE FROM model_routes WHERE rowid NOT IN (" +
                "  SELECT rowid FROM (SELECT rowid, lower(\"ModelId\") AS k, MAX(\"UpdatedAt\") FROM model_routes GROUP BY k));");

            migrationBuilder.AlterColumn<string>(
                name: "ModelId",
                table: "model_routes",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "daily_usage_rollups",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CostCenter",
                table: "daily_usage_rollups",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CostCenter",
                table: "billing_events",
                type: "TEXT",
                maxLength: 128,
                nullable: true,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Merged rollup rows and dropped case-duplicate routes are not restored; the sentinels
            // are turned back into NULLs once the columns are nullable again (see the end).
            migrationBuilder.AlterColumn<string>(
                name: "ModelId",
                table: "model_routes",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256,
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "daily_usage_rollups",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "CostCenter",
                table: "daily_usage_rollups",
                type: "TEXT",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "CostCenter",
                table: "billing_events",
                type: "TEXT",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 128,
                oldNullable: true,
                oldCollation: "NOCASE");

            migrationBuilder.Sql(
                $"UPDATE daily_usage_rollups SET \"TenantId\" = NULL WHERE \"TenantId\" = '{AnonymousTenantId}';");
            migrationBuilder.Sql(
                "UPDATE daily_usage_rollups SET \"CostCenter\" = NULL WHERE \"CostCenter\" = '';");
        }
    }
}
