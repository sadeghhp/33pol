using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApiKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CostCenter = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PromptTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    InputCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    OutputCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    TotalCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    DurationMs = table.Column<double>(type: "REAL", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "daily_usage_rollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsageDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CostCenter = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PromptTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    RequestCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_usage_rollups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gateway_stats_snapshot",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalRequests = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalErrors = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalLatencyMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RateLimitRejections = table.Column<long>(type: "INTEGER", nullable: false),
                    QuotaRejections = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestsPerModelJson = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorsPerModelJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_stats_snapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RateCardSlug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MonthlyTokenLimit = table.Column<long>(type: "INTEGER", nullable: true),
                    RequestsPerMinute = table.Column<int>(type: "INTEGER", nullable: true),
                    ConcurrencyLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quota_allocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TokenLimit = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestLimit = table.Column<long>(type: "INTEGER", nullable: false),
                    SoftLimitRatio = table.Column<decimal>(type: "TEXT", precision: 5, scale: 4, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_allocations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quota_usage_snapshots",
                columns: table => new
                {
                    PartitionKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Period = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Used = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_usage_snapshots", x => x.PartitionKey);
                });

            migrationBuilder.CreateTable(
                name: "quota_usages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    UsedTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedRequests = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_usages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rate_cards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    InputPricePerMillionTokens = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    OutputPricePerMillionTokens = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EffectiveUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_cards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "recent_requests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<double>(type: "REAL", nullable: false),
                    IsStreaming = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recent_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PlanSlug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CostCenter = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    KeyPrefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Scopes = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Label = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Assignee = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CostCenter = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_keys_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "budgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AmountLimit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    WarningThresholdRatio = table.Column<decimal>(type: "TEXT", precision: 5, scale: 4, nullable: false),
                    HardStopEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PeriodStartDay = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budgets_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "model_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelPattern = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Effect = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_model_grants_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_key_model_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelPattern = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Effect = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key_model_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_key_model_grants_api_keys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "api_keys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_key_model_grants_ApiKeyId_ModelPattern",
                table: "api_key_model_grants",
                columns: new[] { "ApiKeyId", "ModelPattern" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_KeyPrefix",
                table: "api_keys",
                column: "KeyPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_TenantId",
                table: "api_keys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_billing_events_ApiKeyId_RecordedAt",
                table: "billing_events",
                columns: new[] { "ApiKeyId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_billing_events_CostCenter_RecordedAt",
                table: "billing_events",
                columns: new[] { "CostCenter", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_billing_events_RequestId",
                table: "billing_events",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_events_TenantId_RecordedAt",
                table: "billing_events",
                columns: new[] { "TenantId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_budgets_TenantId",
                table: "budgets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_daily_usage_rollups_UsageDate_TenantId_ModelId_CostCenter",
                table: "daily_usage_rollups",
                columns: new[] { "UsageDate", "TenantId", "ModelId", "CostCenter" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_grants_TenantId_ModelPattern",
                table: "model_grants",
                columns: new[] { "TenantId", "ModelPattern" });

            migrationBuilder.CreateIndex(
                name: "IX_plans_Slug",
                table: "plans",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quota_allocations_TenantId_PeriodStart_PeriodEnd",
                table: "quota_allocations",
                columns: new[] { "TenantId", "PeriodStart", "PeriodEnd" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quota_usages_TenantId_PeriodStart",
                table: "quota_usages",
                columns: new[] { "TenantId", "PeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rate_cards_ModelId_EffectiveFrom",
                table: "rate_cards",
                columns: new[] { "ModelId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_rate_cards_Slug",
                table: "rate_cards",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recent_requests_TimestampUtc",
                table: "recent_requests",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Slug",
                table: "tenants",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key_model_grants");

            migrationBuilder.DropTable(
                name: "billing_events");

            migrationBuilder.DropTable(
                name: "budgets");

            migrationBuilder.DropTable(
                name: "daily_usage_rollups");

            migrationBuilder.DropTable(
                name: "gateway_stats_snapshot");

            migrationBuilder.DropTable(
                name: "model_grants");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "quota_allocations");

            migrationBuilder.DropTable(
                name: "quota_usage_snapshots");

            migrationBuilder.DropTable(
                name: "quota_usages");

            migrationBuilder.DropTable(
                name: "rate_cards");

            migrationBuilder.DropTable(
                name: "recent_requests");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
