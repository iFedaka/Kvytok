using Kvytok.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kvytok.Api.IntegrationTests.Infrastructure;

// A drain claims every stale row in the shared database, so drains in parallel tests would hold each other's rows
[Collection(OutboxDrains.Collection)]
public sealed class OutboxRelayTests(ApiFixture api)
{
    [Fact]
    public async Task A_Drain_Whose_Claim_Runs_Out_Stops_And_Leaves_The_Rest_For_The_Next_Drain()
    {
        var run = Guid.NewGuid().ToString();
        await api.WithDbAsync(db =>
        {
            // older than anything other tests leave owed, so these are claimed first and sent in this order
            db.OutboxMessages.AddRange(Enumerable.Range(1, 3).Select(n => new OutboxMessage
            {
                MessageId = $"{run}:{n}",
                Subject = "RelayTest",
                Body = "{}",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1).AddSeconds(n),
            }));
            return db.SaveChangesAsync();
        });
        var throttled = true;
        // far past the claim, so only the claim's own deadline can end it
        api.ReservationsQueue.StallWhen(m => throttled && m.MessageId == $"{run}:1", TimeSpan.FromMinutes(1));

        await api.Relay.DrainAsync(TestContext.Current.CancellationToken);

        // the first send was cut off when the claim ran out, and the other two were never tried
        Assert.DoesNotContain(api.ReservationsQueue.Sent, m => m.MessageId.StartsWith(run));
        var rows = await RowsAsync(run);
        Assert.All(rows, r => Assert.Null(r.PublishedAt));
        Assert.All(rows, r => Assert.NotNull(r.ClaimedAt));

        throttled = false;
        // the real lease, in the database's time: nothing here backdates the claim
        await Task.Delay(ApiFixture.OutboxLease + TimeSpan.FromSeconds(0.5), TestContext.Current.CancellationToken);
        await api.Relay.DrainAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [$"{run}:1", $"{run}:2", $"{run}:3"],
            api.ReservationsQueue.Sent.Where(m => m.MessageId.StartsWith(run)).Select(m => m.MessageId));
        Assert.All(await RowsAsync(run), r => Assert.Equal(api.Clock.GetUtcNow(), r.PublishedAt));
    }

    private Task<List<OutboxMessage>> RowsAsync(string run) =>
        api.WithDbAsync(db => db.OutboxMessages
            .Where(m => m.MessageId.StartsWith(run))
            .OrderBy(m => m.Id)
            .ToListAsync());
}

public static class OutboxDrains
{
    public const string Collection = "Outbox drains";
}
