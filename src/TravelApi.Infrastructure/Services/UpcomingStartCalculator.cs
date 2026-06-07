using Microsoft.EntityFrameworkCore;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-019 (avisos "Proximos inicios", 2026-06-06): calcula el PRIMER INICIO de una reserva como el
/// MIN de las fechas de inicio de sus servicios <b>NO cancelados</b>, sobre los 6 tipos (Hotel CheckIn,
/// Paquete StartDate, Aereo DepartureTime, Traslado PickupDateTime, Asistencia ValidFrom, generico
/// DepartureDate). Devuelve fecha "de pared" date-only con Kind=Utc.
///
/// <para><b>OJO — en el sistema coexisten TRES definiciones de "cuando empieza" la reserva, A PROPOSITO
/// (ADR-019 R8). NO las unifiques:</b></para>
/// <list type="number">
///   <item><see cref="ReservaScheduleCalculator"/>: MIN/MAX <b>CON</b> servicios cancelados. Alimenta el
///   StartDate/EndDate persistidos de la Reserva y con eso el lifecycle (historico, mueve estados).</item>
///   <item>Este helper: MIN <b>SIN</b> cancelados. Alimenta el aviso de la campanita y el endpoint de
///   dismiss — un servicio cancelado no debe disparar avisos (Ronda 5 de la guia UX).</item>
///   <item>El job <c>ReservaLifecycleAutomationService.AutoTransitionConfirmedToTravelingAsync</c> decide
///   la promocion Confirmed → Traveling con el StartDate <b>PERSISTIDO</b> (el MIN con cancelados, ademas
///   editable a mano) — no con ninguno de los dos calculos en vivo. Deuda preexistente nombrada en el
///   ADR: puede promover antes de tiempo si el servicio mas temprano esta cancelado.</item>
/// </list>
///
/// <para>Es EL UNICO lugar donde se define "primer inicio" para el aviso: lo usan el bucket
/// <c>upcomingStarts</c> de <see cref="AlertService"/> Y el endpoint de dismiss — si difirieran,
/// el "Listo" podria anclar una fecha distinta de la que muestra la campanita y el re-armado
/// de ADR-019 D3 se romperia en silencio.</para>
/// </summary>
public static class UpcomingStartCalculator
{
    /// <summary>
    /// Primer inicio de UNA reserva (para el endpoint de dismiss). Null = sin servicios elegibles
    /// (sin servicios, o todos cancelados).
    /// </summary>
    public static async Task<DateTime?> ComputeFirstStartAsync(
        AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var byReserva = await ComputeFirstStartsAsync(db, new[] { reservaId }, maxStartDateInclusive: null, ct);
        return byReserva.TryGetValue(reservaId, out var firstStart) ? firstStart : null;
    }

    /// <summary>
    /// Primer inicio de un LOTE de reservas (para el bucket de alertas). Devuelve solo las reservas
    /// que tienen al menos un servicio elegible.
    ///
    /// <para><c>maxStartDateInclusive</c> es una OPTIMIZACION pura, no cambia la definicion: si el
    /// verdadero MIN cae dentro del tope, el MIN sobre las filas filtradas es el mismo (toda fecha
    /// menor al MIN tambien pasa el filtro); si el verdadero MIN cae despues del tope, la reserva
    /// no aparece en el resultado — y el caller la iba a descartar igual por ventana. El tope se
    /// compara EXCLUSIVO contra la medianoche del dia siguiente para no perder servicios con hora
    /// (un vuelo a las 22:00 del ultimo dia de la ventana debe entrar).</para>
    /// </summary>
    public static async Task<Dictionary<int, DateTime>> ComputeFirstStartsAsync(
        AppDbContext db,
        IReadOnlyCollection<int> reservaIds,
        DateTime? maxStartDateInclusive,
        CancellationToken ct = default)
    {
        var earliestByReserva = new Dictionary<int, DateTime>();
        if (reservaIds.Count == 0) return earliestByReserva;

        // Tope exclusivo: "hasta el dia X inclusive" = "antes de la medianoche de X+1".
        DateTime? upperExclusive = maxStartDateInclusive?.Date.AddDays(1);

        // Una consulta GROUP BY (ReservaId, MIN(fecha)) por tipo, con el predicado de no-cancelado
        // INLINE (EF Core no traduce helpers propios dentro de la query). El MIN se toma sobre el
        // datetime crudo: si dos servicios tienen fechas distintas, el de fecha menor tiene tambien
        // el datetime menor, asi que MIN(datetime).Date == MIN(fechas) — sin riesgo de corrimiento.

        // Hotel: empieza en el CheckIn.
        var hotelStarts = await db.HotelBookings
            .Where(h => reservaIds.Contains(h.ReservaId)
                        && h.Status != "Cancelado"
                        && (upperExclusive == null || h.CheckIn < upperExclusive))
            .GroupBy(h => h.ReservaId)
            .Select(g => new { ReservaId = g.Key, Start = g.Min(h => h.CheckIn) })
            .ToListAsync(ct);
        foreach (var row in hotelStarts) KeepEarliest(earliestByReserva, row.ReservaId, row.Start);

        // Paquete: empieza en StartDate.
        var packageStarts = await db.PackageBookings
            .Where(p => reservaIds.Contains(p.ReservaId)
                        && p.Status != "Cancelado"
                        && (upperExclusive == null || p.StartDate < upperExclusive))
            .GroupBy(p => p.ReservaId)
            .Select(g => new { ReservaId = g.Key, Start = g.Min(p => p.StartDate) })
            .ToListAsync(ct);
        foreach (var row in packageStarts) KeepEarliest(earliestByReserva, row.ReservaId, row.Start);

        // Aereo: empieza en DepartureTime (hora de pared del aeropuerto con Kind=Utc — contrato
        // NormalizeAirportWallClock). Cancelado aereo = estados UN/UC/HX/NO (WorkflowStatusHelper).
        // No hace falta agrupar por PNR: el MIN por reserva es el mismo se agrupe o no.
        var flightStarts = await db.FlightSegments
            .Where(f => reservaIds.Contains(f.ReservaId)
                        && f.Status != "UN" && f.Status != "UC" && f.Status != "HX" && f.Status != "NO"
                        && (upperExclusive == null || f.DepartureTime < upperExclusive))
            .GroupBy(f => f.ReservaId)
            .Select(g => new { ReservaId = g.Key, Start = g.Min(f => f.DepartureTime) })
            .ToListAsync(ct);
        foreach (var row in flightStarts) KeepEarliest(earliestByReserva, row.ReservaId, row.Start);

        // Traslado: empieza en PickupDateTime.
        var transferStarts = await db.TransferBookings
            .Where(t => reservaIds.Contains(t.ReservaId)
                        && t.Status != "Cancelado"
                        && (upperExclusive == null || t.PickupDateTime < upperExclusive))
            .GroupBy(t => t.ReservaId)
            .Select(g => new { ReservaId = g.Key, Start = g.Min(t => t.PickupDateTime) })
            .ToListAsync(ct);
        foreach (var row in transferStarts) KeepEarliest(earliestByReserva, row.ReservaId, row.Start);

        // Asistencia: empieza cuando arranca la vigencia (ValidFrom).
        var assistanceStarts = await db.AssistanceBookings
            .Where(a => reservaIds.Contains(a.ReservaId)
                        && a.Status != "Cancelado"
                        && (upperExclusive == null || a.ValidFrom < upperExclusive))
            .GroupBy(a => a.ReservaId)
            .Select(g => new { ReservaId = g.Key, Start = g.Min(a => a.ValidFrom) })
            .ToListAsync(ct);
        foreach (var row in assistanceStarts) KeepEarliest(earliestByReserva, row.ReservaId, row.Start);

        // Servicio generico: empieza en DepartureDate. ReservaId es nullable en esta entidad.
        var genericStarts = await db.Servicios
            .Where(s => s.ReservaId != null && reservaIds.Contains(s.ReservaId.Value)
                        && s.Status != "Cancelado"
                        && (upperExclusive == null || s.DepartureDate < upperExclusive))
            .GroupBy(s => s.ReservaId!.Value)
            .Select(g => new { ReservaId = g.Key, Start = g.Min(s => s.DepartureDate) })
            .ToListAsync(ct);
        foreach (var row in genericStarts) KeepEarliest(earliestByReserva, row.ReservaId, row.Start);

        return earliestByReserva;
    }

    /// <summary>
    /// Guarda en el acumulado la fecha mas temprana entre lo que ya habia y la nueva. Normaliza a
    /// date-only "de pared" Kind=Utc: la hora se descarta SIN convertir el instante (las fechas vienen
    /// guardadas como hora de pared con Kind=Utc — contrato declarado en ADR-019 §1/M3 — y convertir
    /// de zona correria el dia para horarios nocturnos, p. ej. un vuelo a las 22:00 ART).
    /// </summary>
    private static void KeepEarliest(Dictionary<int, DateTime> earliestByReserva, int reservaId, DateTime start)
    {
        var startDate = DateTime.SpecifyKind(start.Date, DateTimeKind.Utc);
        if (!earliestByReserva.TryGetValue(reservaId, out var current) || startDate < current)
        {
            earliestByReserva[reservaId] = startDate;
        }
    }
}
