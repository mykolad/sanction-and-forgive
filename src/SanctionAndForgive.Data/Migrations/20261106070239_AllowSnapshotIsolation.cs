using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanctionAndForgive.Data.Migrations
{
    /// <summary>The API reads a list (its import, subjects, names and addresses) in one snapshot transaction, so an
    /// import cleanup running at the same time can't hand it half a list. Azure SQL Database allows snapshot isolation
    /// by default, so there this changes nothing (and needs no ALTER DATABASE right); a local or CI SQL Server needs
    /// it turned on. Outside a transaction: ALTER DATABASE can't run in one.</summary>
    public partial class AllowSnapshotIsolation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF (SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()) = 0
                    ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;
                """,
                suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately left on: turning it off would break the API's reads, and it costs nothing unused.
        }
    }
}
