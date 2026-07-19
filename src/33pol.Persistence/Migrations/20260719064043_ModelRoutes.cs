using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    MaxContextLength = table.Column<int>(type: "INTEGER", nullable: false),
                    Aliases = table.Column<string>(type: "TEXT", nullable: false),
                    Capabilities = table.Column<string>(type: "TEXT", nullable: false),
                    PublicAccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpstreamAuthJson = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_routes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_routes_ModelId",
                table: "model_routes",
                column: "ModelId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_routes");
        }
    }
}
