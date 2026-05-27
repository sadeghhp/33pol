using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations;

/// <inheritdoc />
public partial class ApiKeyModelGrants : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "api_key_model_grants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                ModelPattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Effect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
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
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "api_key_model_grants");
    }
}
