using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fors.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SetupMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SetupCompletedAt",
                table: "SignInSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetupNonce",
                table: "SignInSettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SetupOpenedAt",
                table: "SignInSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetupReopenConsumed",
                table: "SignInSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Setup mode treats a missing row as a fault and stays closed, so every database has it from here on (app-roles.sql
            // inserts it too, for the grants); a never-completed row is a first run.
            migrationBuilder.Sql("""INSERT INTO "SignInSettings" ("Id", "ExtraScopes") VALUES (1, ARRAY[]::text[]) ON CONFLICT ("Id") DO NOTHING;""");

            migrationBuilder.CreateTable(
                name: "SetupSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    OpeningNonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SetupTests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarkerHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    BrowserHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Protected = table.Column<string>(type: "text", nullable: false),
                    ValuesFingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CallbackAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: true),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Result = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupTests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SetupTests_SetupSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "SetupSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SetupSessions_TokenHash",
                table: "SetupSessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SetupTests_MarkerHash",
                table: "SetupTests",
                column: "MarkerHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SetupTests_SessionId",
                table: "SetupTests",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SetupTests");

            migrationBuilder.DropTable(
                name: "SetupSessions");

            migrationBuilder.DropColumn(
                name: "SetupCompletedAt",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SetupNonce",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SetupOpenedAt",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SetupReopenConsumed",
                table: "SignInSettings");
        }
    }
}
