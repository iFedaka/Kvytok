using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddSqlServerDbContext<KvytokDbContext>("kvytok",
    configureDbContextOptions: o => o.AddKvytokInterceptors());

// Set by the AppHost in Azure only; see DatabaseRoles there
var readWriteUsers = builder.Configuration.GetSection("Database:ReadWriteUsers").Get<string[]>() ?? [];

using var host = builder.Build();

// Started so telemetry is exported, then stopped so the process exits and dependents can proceed
await host.StartAsync();
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KvytokDbContext>();
    await db.Database.MigrateAsync();

    foreach (var user in readWriteUsers)
    {
        await NarrowToReadWriteAsync(db, user);
    }

    // Nobody but Aspire's SQL admin identity can sign in to look, so the job's log is where to check
    var memberships = await db.Database.SqlQuery<RoleMembership>($"""
        SELECT m.name AS Member, r.name AS Role
        FROM sys.database_role_members rm
        JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id
        JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id
        WHERE m.type = 'E'
        """).ToListAsync();
    host.Services.GetRequiredService<ILogger<Program>>().LogInformation(
        "Database role members: {Memberships}",
        string.Join("; ", memberships.OrderBy(x => x.Member).ThenBy(x => x.Role).Select(x => $"{x.Member} in {x.Role}")));
}
await host.StopAsync();

// Aspire's role script makes the user db_owner whenever it runs; this takes it back to reading and
// writing rows, in one transaction, so a failure leaves the user in db_owner and the next run tries
// again, never in all three roles. The narrower roles are added before db_owner is dropped. Each
// step checks membership first, so a re-run is a no-op. A missing user fails the run: the name is
// wrong, and carrying on would leave whoever it should have been still db_owner.
static Task NarrowToReadWriteAsync(KvytokDbContext db, string user) =>
    db.Database.ExecuteSqlAsync($"""
        DECLARE @user SYSNAME = {user};
        DECLARE @id INT = DATABASE_PRINCIPAL_ID(@user);
        IF @id IS NULL
            THROW 50000, 'No database user to narrow; see Database:ReadWriteUsers', 1;

        DECLARE @sql NVARCHAR(MAX);

        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        IF NOT EXISTS (SELECT 1 FROM sys.database_role_members WHERE member_principal_id = @id AND role_principal_id = DATABASE_PRINCIPAL_ID(N'db_datareader'))
        BEGIN
            SET @sql = N'ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@user);
            EXEC (@sql);
        END

        IF NOT EXISTS (SELECT 1 FROM sys.database_role_members WHERE member_principal_id = @id AND role_principal_id = DATABASE_PRINCIPAL_ID(N'db_datawriter'))
        BEGIN
            SET @sql = N'ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@user);
            EXEC (@sql);
        END

        IF EXISTS (SELECT 1 FROM sys.database_role_members WHERE member_principal_id = @id AND role_principal_id = DATABASE_PRINCIPAL_ID(N'db_owner'))
        BEGIN
            SET @sql = N'ALTER ROLE db_owner DROP MEMBER ' + QUOTENAME(@user);
            EXEC (@sql);
        END

        COMMIT TRANSACTION;
        """);

internal sealed record RoleMembership(string Member, string Role);
