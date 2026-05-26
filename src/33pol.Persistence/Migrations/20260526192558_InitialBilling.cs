using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CostCenter = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PromptTokens = table.Column<long>(type: "bigint", nullable: false),
                    CompletionTokens = table.Column<long>(type: "bigint", nullable: false),
                    InputCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    OutputCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    TotalCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "budgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AmountLimit = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    WarningThresholdRatio = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    HardStopEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PeriodStartDay = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
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
                name: "plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RateCardSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MonthlyTokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    RequestsPerMinute = table.Column<int>(type: "integer", nullable: true),
                    ConcurrencyLimit = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rate_cards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    InputPricePerMillionTokens = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    OutputPricePerMillionTokens = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    EffectiveUntil = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_cards", x => x.Id);
                });

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
                name: "IX_plans_Slug",
                table: "plans",
                column: "Slug",
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_events");

            migrationBuilder.DropTable(
                name: "budgets");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "rate_cards");
        }
    }
}
