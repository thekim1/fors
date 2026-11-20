using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fors.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The rename to Fors (0.12.0): the retention function <c>ume_retention_step</c> becomes <c>fors_retention_step</c>.
    /// Renaming keeps its owner, body and grants (only the admin role may execute it). The database roles are renamed by
    /// the deployment (deploy/upgrade/0.12-rename-roles.sql), not here: the migrator may not rename roles.
    /// </summary>
    public partial class ForsRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER FUNCTION ume_retention_step(integer) RENAME TO fors_retention_step;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER FUNCTION fors_retention_step(integer) RENAME TO ume_retention_step;""");
        }
    }
}
