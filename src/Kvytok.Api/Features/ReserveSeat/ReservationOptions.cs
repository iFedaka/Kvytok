namespace Kvytok.Api.Features.ReserveSeat;

/// <summary>Bound from the "Reservation" section.</summary>
public sealed class ReservationOptions
{
    public const string Section = "Reservation";

    /// <summary>
    /// How long a seat stays held without payment. Configuration rather than a constant because
    /// environments want different numbers: ten minutes as described, seconds for a demo.
    /// </summary>
    public TimeSpan HoldFor { get; set; }
}
