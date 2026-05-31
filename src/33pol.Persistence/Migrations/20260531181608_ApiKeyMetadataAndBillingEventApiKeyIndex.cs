using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeyMetadataAndBillingEventApiKeyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Assignee",
                table: "api_keys",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostCenter",
                table: "api_keys",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "api_keys",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "api_keys",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_events_ApiKeyId_RecordedAt",
                table: "billing_events",
                columns: new[] { "ApiKeyId", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_billing_events_ApiKeyId_RecordedAt",
                table: "billing_events");

            migrationBuilder.DropColumn(
                name: "Assignee",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "CostCenter",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "api_keys");
        }
    }
}
