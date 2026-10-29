using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kvytok.Domain.Migrations
{
    /// <inheritdoc />
    public partial class ReadCommittedSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Azure SQL has this on by default; SQL Server, locally and in the tests, does not.
            // Reserving a seat reads its status before locking it, and that read must not wait
            // on the winner's lock. Checked first so Azure, already on, needs no ALTER permission.
            // ALTER DATABASE cannot run inside a transaction.
            migrationBuilder.Sql("""
                IF (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()) = 0
                    EXEC('ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE');
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // left on: it may have been on before Up, as it is on Azure SQL
        }
    }
}
