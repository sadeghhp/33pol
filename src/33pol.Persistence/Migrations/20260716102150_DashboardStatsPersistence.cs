using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DashboardStatsPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_stats_snapshot",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    TotalRequests = table.Column<long>(type: "bigint", nullable: false),
                    TotalErrors = table.Column<long>(type: "bigint", nullable: false),
                    TotalLatencyMs = table.Column<long>(type: "bigint", nullable: false),
                    RateLimitRejections = table.Column<long>(type: "bigint", nullable: false),
                    QuotaRejections = table.Column<long>(type: "bigint", nullable: false),
                    RequestsPerModelJson = table.Column<string>(type: "jsonb", nullable: false),
                    ErrorsPerModelJson = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_stats_snapshot", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quota_usage_snapshots",
                columns: table => new
                {
                    PartitionKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Used = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_usage_snapshots", x => x.PartitionKey);
                });

            migrationBuilder.CreateTable(
                name: "recent_requests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TenantId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    IsStreaming = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recent_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recent_requests_TimestampUtc",
                table: "recent_requests",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_stats_snapshot");

            migrationBuilder.DropTable(
                name: "quota_usage_snapshots");

            migrationBuilder.DropTable(
                name: "recent_requests");
        }
    }
}
