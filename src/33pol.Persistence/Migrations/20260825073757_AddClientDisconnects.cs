using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClientDisconnects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ClientDisconnects",
                table: "gateway_stats_snapshot",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientDisconnects",
                table: "gateway_stats_snapshot");
        }
    }
}
