using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RateLimitConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rate_limit_defaults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Rpm = table.Column<int>(type: "INTEGER", nullable: false),
                    Burst = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxConcurrentStreams = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_limit_defaults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rate_limit_plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Rpm = table.Column<int>(type: "INTEGER", nullable: false),
                    Burst = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxConcurrentStreams = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_limit_plans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rate_limit_plans_Slug",
                table: "rate_limit_plans",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_limit_defaults");

            migrationBuilder.DropTable(
                name: "rate_limit_plans");
        }
    }
}
