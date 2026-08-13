using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GatewayErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_errors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EventCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ExceptionType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    StackTrace = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    Method = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    RouteKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpstreamTarget = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 48, nullable: true),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ApiKeyId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DurationMs = table.Column<double>(type: "REAL", nullable: true),
                    UpstreamBodySnippet = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Hint = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_errors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_errors_Fingerprint_OccurredAt",
                table: "gateway_errors",
                columns: new[] { "Fingerprint", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_errors_ModelId_OccurredAt",
                table: "gateway_errors",
                columns: new[] { "ModelId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_errors_OccurredAt",
                table: "gateway_errors",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_errors_RecordId",
                table: "gateway_errors",
                column: "RecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_errors_RequestId",
                table: "gateway_errors",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_errors_StatusCode_OccurredAt",
                table: "gateway_errors",
                columns: new[] { "StatusCode", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_errors");
        }
    }
}
