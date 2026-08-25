using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelRouteState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "model_routes",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "serving");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "State",
                table: "model_routes");
        }
    }
}
