using System.Diagnostics.Metrics;

namespace Kvytok.Api.Features.ReserveSeat;

/// <summary>
/// Counts reservation attempts that reached a seat, by how they ended. Exported once a minute, so
/// each export is the minute's count; the dashboard reads it as reservations per minute.
/// </summary>
public sealed class ReservationMetrics(IMeterFactory meters)
{
    // The app's own meter, which ServiceDefaults already exports
    public static readonly string MeterName = typeof(ReservationMetrics).Assembly.GetName().Name!;
    public const string InstrumentName = "kvytok.reservations";
    public const string OutcomeTag = "kvytok.reservation.outcome";

    private readonly Counter<long> _reservations = meters.Create(MeterName).CreateCounter<long>(
        InstrumentName,
        unit: "{reservation}",
        description: "Reservation attempts for an existing seat, by outcome: held, or taken by someone else.");

    public void Held() => _reservations.Add(1, new KeyValuePair<string, object?>(OutcomeTag, "held"));

    // A lost race is the system working, not an error: counted here so contention shows beside the holds
    public void SeatTaken() => _reservations.Add(1, new KeyValuePair<string, object?>(OutcomeTag, "seat_taken"));
}
