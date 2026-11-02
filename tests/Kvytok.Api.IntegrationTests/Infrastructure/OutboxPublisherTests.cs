using Kvytok.Api.Infrastructure;
using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kvytok.Api.IntegrationTests.Infrastructure;

// Runs apart from tests that drain the outbox: a drain sends every owed row, including the ones this test checks were not sent
[Collection(OutboxDrains.Collection)]
public sealed class OutboxPublisherTests(ApiFixture api)
{
    [Fact]
    public async Task Publisher_With_Failure_Injection_On_Sends_Nothing_And_Leaves_The_Rows_Owed()
    {
        var run = Guid.NewGuid().ToString();
        var options = Options.Create(new OutboxOptions { Lease = ApiFixture.OutboxLease, FailImmediatePublish = true });

        await api.WithDbAsync(async db =>
        {
            var rows = Enumerable.Range(1, 2)
                .Select(n => new OutboxMessage { MessageId = $"{run}:{n}", Subject = "PublisherTest", Body = "{}" })
                .ToList();
            db.OutboxMessages.AddRange(rows);
            await db.SaveChangesAsync();

            var publisher = new OutboxPublisher(
                api.ReservationsQueue, db, api.Clock, options, NullLogger<OutboxPublisher>.Instance);
            await publisher.PublishAsync(rows);
            return 0;
        });

        Assert.DoesNotContain(api.ReservationsQueue.Sent, m => m.MessageId.StartsWith(run));
        var owed = await api.WithDbAsync(db => db.OutboxMessages
            .Where(m => m.MessageId.StartsWith(run))
            .ToListAsync());
        Assert.Equal(2, owed.Count);
        Assert.All(owed, r => Assert.Null(r.PublishedAt));
    }
}
