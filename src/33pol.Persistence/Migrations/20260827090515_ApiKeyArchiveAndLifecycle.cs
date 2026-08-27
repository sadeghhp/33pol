using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pol33.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeyArchiveAndLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_api_keys_TenantId",
                table: "api_keys");

            migrationBuilder.AddColumn<long>(
                name: "ArchivedAt",
                table: "api_keys",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "api_key_lifecycle_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeyPrefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Event = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorApiKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    HadUsage = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key_lifecycle_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_TenantId_ArchivedAt_CreatedAt",
                table: "api_keys",
                columns: new[] { "TenantId", "ArchivedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_api_key_lifecycle_events_ApiKeyId_OccurredAt",
                table: "api_key_lifecycle_events",
                columns: new[] { "ApiKeyId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_api_key_lifecycle_events_TenantId_OccurredAt",
                table: "api_key_lifecycle_events",
                columns: new[] { "TenantId", "OccurredAt" });

            // Backfill, so the history view is not blank for every key that already exists. Without
            // it, "when was this key created / who revoked it" is answerable only for keys minted
            // after this migration, which is precisely the population an audit is least interested in.
            // Actor is left null: nothing recorded who acted before this table existed, and inventing
            // one would be worse than an honest gap.
            migrationBuilder.Sql($"""
                INSERT INTO api_key_lifecycle_events
                    (Id, ApiKeyId, TenantId, KeyPrefix, Label, Event, OccurredAt, ActorApiKeyId, Reason, HadUsage)
                SELECT {NewGuidSql}, Id, TenantId, KeyPrefix, Label, 'Created', CreatedAt, NULL, NULL,
                       CASE WHEN LastUsedAt IS NULL THEN 0 ELSE 1 END
                FROM api_keys;
                """);

            migrationBuilder.Sql($"""
                INSERT INTO api_key_lifecycle_events
                    (Id, ApiKeyId, TenantId, KeyPrefix, Label, Event, OccurredAt, ActorApiKeyId, Reason, HadUsage)
                SELECT {NewGuidSql}, Id, TenantId, KeyPrefix, Label, 'Revoked', RevokedAt, NULL, NULL,
                       CASE WHEN LastUsedAt IS NULL THEN 0 ELSE 1 END
                FROM api_keys
                WHERE RevokedAt IS NOT NULL;
                """);
        }

        /// <summary>
        /// A version-4 UUID in the dashed UPPERCASE form the provider writes.
        /// </summary>
        /// <remarks>
        /// Case matters. EF Core's SQLite provider binds a <see cref="Guid"/> parameter as uppercase
        /// text, and SQLite compares TEXT with a case-sensitive collation — so a lowercase id here
        /// would insert cleanly, read back as a valid Guid, and then match nothing. <c>hex()</c> is
        /// already uppercase; the point is not to wrap this in <c>lower()</c>.
        /// </remarks>
        private const string NewGuidSql =
            "substr(hex(randomblob(4)), 1, 8) || '-' || " +
            "substr(hex(randomblob(2)), 1, 4) || '-4' || " +
            "substr(hex(randomblob(2)), 2, 3) || '-' || " +
            "substr('89AB', abs(random()) % 4 + 1, 1) || " +
            "substr(hex(randomblob(2)), 2, 3) || '-' || " +
            "hex(randomblob(6))";

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_key_lifecycle_events");

            migrationBuilder.DropIndex(
                name: "IX_api_keys_TenantId_ArchivedAt_CreatedAt",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "api_keys");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_TenantId",
                table: "api_keys",
                column: "TenantId");
        }
    }
}
