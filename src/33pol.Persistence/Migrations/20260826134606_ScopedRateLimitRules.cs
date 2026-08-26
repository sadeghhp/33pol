using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopedRateLimitRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdaptiveEnabled",
                table: "rate_limit_defaults",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "rate_limit_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TargetKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "NOCASE"),
                    Rpm = table.Column<int>(type: "INTEGER", nullable: false),
                    Burst = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxConcurrentStreams = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_limit_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rate_limit_rules_Scope_TargetKey",
                table: "rate_limit_rules",
                columns: new[] { "Scope", "TargetKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_limit_rules");

            migrationBuilder.DropColumn(
                name: "AdaptiveEnabled",
                table: "rate_limit_defaults");
        }
    }
}
