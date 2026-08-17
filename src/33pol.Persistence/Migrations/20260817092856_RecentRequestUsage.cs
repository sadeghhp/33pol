using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecentRequestUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CompletionTokens",
                table: "recent_requests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostCenter",
                table: "recent_requests",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "recent_requests",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InputCost",
                table: "recent_requests",
                type: "TEXT",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutputCost",
                table: "recent_requests",
                type: "TEXT",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricingStatus",
                table: "recent_requests",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PromptTokens",
                table: "recent_requests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenSource",
                table: "recent_requests",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalCost",
                table: "recent_requests",
                type: "TEXT",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotalTokens",
                table: "recent_requests",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "recent_requests");

            migrationBuilder.DropColumn(
                name: "CostCenter",
                table: "recent_requests");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "recent_requests");

            migrationBuilder.DropColumn(
                name: "InputCost",
                table: "recent_requests");

            migrationBuilder.DropColumn(
                name: "OutputCost",
                table: "recent_requests");

            migrationBuilder.DropColumn(
                name: "PricingStatus",
                table: "recent_requests");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "recent_requests");

            migrationBuilder.DropColumn(
                name: "TokenSource",
                table: "recent_requests");

            migrationBuilder.DropColumn(
                name: "TotalCost",
                table: "recent_requests");

            migrationBuilder.DropColumn(
                name: "TotalTokens",
                table: "recent_requests");
        }
    }
}
