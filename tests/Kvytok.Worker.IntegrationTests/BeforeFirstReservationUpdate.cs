using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kvytok.Worker.IntegrationTests;

/// <summary>
/// Runs <paramref name="interloper"/> once, after the lifecycle has read the reservation and just
/// before it writes it: the exact window a payment and an expiry race in.
/// </summary>
public sealed class BeforeFirstReservationUpdate(Func<Task> interloper) : DbCommandInterceptor
{
    private int _fired;

    // SaveChanges sends its UPDATE as a reader (it reads the new rowversion back)
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await MaybeInterveneAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await MaybeInterveneAsync(command);
        return result;
    }

    private async Task MaybeInterveneAsync(DbCommand command)
    {
        if (command.CommandText.Contains("UPDATE [Reservations]") && Interlocked.Exchange(ref _fired, 1) == 0)
            await interloper();
    }
}
