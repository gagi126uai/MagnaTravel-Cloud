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
///
/// <para><b>OJO — este MIN INCLUYE servicios cancelados, A PROPOSITO. NO lo "arregles" filtrando
/// por Status (ADR-019 R8)</b>: este calculo es historico y alimenta el StartDate persistido que
/// mueve estados (el job <c>AutoTransitionConfirmedToTravelingAsync</c> promueve Confirmed →
/// Traveling comparando ese StartDate persistido contra hoy). Para el aviso "Proximos inicios"
/// de la campanita existe OTRO calculo, <see cref="UpcomingStartCalculator"/>, que EXCLUYE los
/// cancelados — son dos definiciones distintas de "cuando empieza" que coexisten adrede; el
/// comentario de aquel helper explica la tercera (el job). Cambiar el criterio de este MIN es
/// otro alcance y otro riesgo (tocaria el lifecycle entero).</para>
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

        // BUG 2 (2026-06-08): ArrivalTime es nullable (vuelos solo de ida). Si no hay hora de llegada,
        // el "fin" del segmento es su salida — mismo patron que el transfer (ReturnDateTime ?? PickupDateTime)
        // de mas abajo. Asi una reserva con un unico vuelo de ida no queda sin EndDate.
        endDates.AddRange(await db.FlightSegments
            .Where(f => f.ReservaId == reservaId)
            .Select(f => f.ArrivalTime ?? f.DepartureTime)
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

        // ADR-018: EndDate del paquete puede ser null (ficha "producto-primero"). Se coalesce a
        // StartDate — mismo patron que el transfer (ReturnDateTime ?? PickupDateTime) de mas arriba —
        // para no inventar una fecha de fin ni romper el List<DateTime>.
        endDates.AddRange(await db.PackageBookings
            .Where(p => p.ReservaId == reservaId)
            .Select(p => p.EndDate ?? p.StartDate)
            .ToListAsync(ct));

        // Asistencia (seguro): su vigencia ValidFrom/ValidTo entra al min/max de fechas igual
        // que el check-in/out de hotel. Si faltara aca, una reserva que SOLO tenga asistencia
        // quedaria sin StartDate/EndDate y el lifecycle/los chips de fecha fallarian en silencio.
        startDates.AddRange(await db.AssistanceBookings
            .Where(a => a.ReservaId == reservaId)
            .Select(a => a.ValidFrom)
            .ToListAsync(ct));

        endDates.AddRange(await db.AssistanceBookings
            .Where(a => a.ReservaId == reservaId)
            .Select(a => a.ValidTo)
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
