using Microsoft.EntityFrameworkCore;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Calcula las fechas (StartDate / EndDate) de una Reserva tomando el min/max
/// de las fechas de los servicios cargados (vuelos, hoteles, transfers, paquetes
/// y servicios genericos). NO modifica la reserva — solo devuelve la tupla.
///
/// La idea es centralizar este calculo en un solo lugar para que lo puedan
/// reusar:
/// - BookingService.RecalculateReservationScheduleAsync (post-mutation de servicios)
/// - ReservaLifecycleAutomationService (repair de reservas con EndDate=null)
/// - ReservaService al construir el ReservaDto (sugerir fechas en la UI)
/// </summary>
public static class ReservaScheduleCalculator
{
    /// <summary>
    /// Devuelve (Start, End) computados como min/max de fechas de todos los servicios
    /// asociados a la reserva. Si no hay servicios, devuelve (null, null).
    /// Las fechas se devuelven con Kind=Utc para ser persistibles directamente
    /// en columnas Postgres 'timestamp with time zone'.
    /// </summary>
    public static async Task<(DateTime? Start, DateTime? End)> ComputeAsync(
        AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var startDates = new List<DateTime>();
        var endDates = new List<DateTime>();

        startDates.AddRange(await db.FlightSegments
            .Where(f => f.ReservaId == reservaId)
            .Select(f => f.DepartureTime)
            .ToListAsync(ct));

        endDates.AddRange(await db.FlightSegments
            .Where(f => f.ReservaId == reservaId)
            .Select(f => f.ArrivalTime)
            .ToListAsync(ct));

        startDates.AddRange(await db.HotelBookings
            .Where(h => h.ReservaId == reservaId)
            .Select(h => h.CheckIn)
            .ToListAsync(ct));

        endDates.AddRange(await db.HotelBookings
            .Where(h => h.ReservaId == reservaId)
            .Select(h => h.CheckOut)
            .ToListAsync(ct));

        startDates.AddRange(await db.TransferBookings
            .Where(t => t.ReservaId == reservaId)
            .Select(t => t.PickupDateTime)
            .ToListAsync(ct));

        endDates.AddRange(await db.TransferBookings
            .Where(t => t.ReservaId == reservaId)
            .Select(t => t.ReturnDateTime ?? t.PickupDateTime)
            .ToListAsync(ct));

        startDates.AddRange(await db.PackageBookings
            .Where(p => p.ReservaId == reservaId)
            .Select(p => p.StartDate)
            .ToListAsync(ct));

        endDates.AddRange(await db.PackageBookings
            .Where(p => p.ReservaId == reservaId)
            .Select(p => p.EndDate)
            .ToListAsync(ct));

        startDates.AddRange(await db.Servicios
            .Where(s => s.ReservaId == reservaId)
            .Select(s => s.DepartureDate)
            .ToListAsync(ct));

        endDates.AddRange(await db.Servicios
            .Where(s => s.ReservaId == reservaId)
            .Select(s => s.ReturnDate ?? s.DepartureDate)
            .ToListAsync(ct));

        DateTime? start = startDates.Count > 0 ? AsUtc(startDates.Min()) : null;
        DateTime? end = endDates.Count > 0 ? AsUtc(endDates.Max()) : null;
        return (start, end);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
