using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Worker.IntegrationTests;

public sealed class KvytokDbContextTests
{
    [Fact]
    public void Refuses_To_Start_Without_The_Outbox_Interceptor()
    {
        // never connected to: the check runs in the constructor
        var options = new DbContextOptionsBuilder<KvytokDbContext>()
            .UseSqlServer("Server=localhost;Database=unused;")
            .Options;

        var ex = Assert.Throws<InvalidOperationException>(() => new KvytokDbContext(options));
        Assert.Contains(nameof(KvytokDbContextOptions.AddKvytokInterceptors), ex.Message);
    }
}
