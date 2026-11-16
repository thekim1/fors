using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ume.LlmGateway.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignInSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignInSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Authority = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RoleClaim = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DepartmentClaim = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TeamClaim = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GroupClaim = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExtraScopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    EncryptedClientSecret = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ClientSecretSetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClientSecretExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInSettings", x => x.Id);
                    table.CheckConstraint("CK_SignInSettings_Singleton", "\"Id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignInSettings");
        }
    }
}
