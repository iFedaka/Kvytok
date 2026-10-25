using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<KvytokDbContext>
{
    public KvytokDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<KvytokDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=kvytok-design;")
            .AddKvytokInterceptors()
            .Options);
}