using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GatewayErrorResponseProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsStreaming",
                table: "gateway_errors",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ResponseBytesForwarded",
                table: "gateway_errors",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TimeToFirstTokenMs",
                table: "gateway_errors",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsStreaming",
                table: "gateway_errors");

            migrationBuilder.DropColumn(
                name: "ResponseBytesForwarded",
                table: "gateway_errors");

            migrationBuilder.DropColumn(
                name: "TimeToFirstTokenMs",
                table: "gateway_errors");
        }
    }
}
