using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Calcula las fechas (StartDate / EndDate) de una Reserva tomando el min/max de las fechas de los
/// servicios VIGENTES (vuelos, hoteles, transfers, paquetes, asistencia y servicios genericos).
/// <see cref="ComputeAsync"/> NO modifica la reserva — solo devuelve la tupla. <see cref="RecalculateAndPersistAsync"/>
/// es el ESCRITOR UNICO que persiste ese calculo en <c>Reserva.StartDate</c>/<c>EndDate</c>.
///
/// <para><b>ADR-053 (2026-08-13)</b>: este calculo EXCLUYE los servicios ANULADOS — reemplazo formal de
/// ADR-019 R8, que hasta esta obra los incluia a proposito (el comentario viejo decia "NO lo arregles
/// filtrando por Status"; esa decision quedo revertida por el dueño el 2026-08-11: la ventana del viaje
/// pasa a ser calculada SOLO desde los servicios vigentes). El calculo "Proximos inicios" de
/// <see cref="UpcomingStartCalculator"/> ya excluia cancelados desde ADR-019; con este cambio, los dos
/// calculos comparten el MISMO predicado de "vigente" (ver <see cref="IsCancelledGenericStatus"/>/
/// <see cref="IsCancelledFlightStatus"/>) — la vieja deuda de "tres definiciones de cuando empieza la
/// reserva" (ADR-019 §6) se reduce a una sola definicion compartida para dos de los tres usos.</para>
/// </summary>
public static class ReservaScheduleCalculator
{
    /// <summary>
    /// Devuelve (Start, End) computados como min/max de fechas de los servicios VIGENTES (no anulados)
    /// asociados a la reserva. Si no hay ningun servicio vigente, devuelve (null, null). Las fechas se
    /// devuelven con Kind=Utc para ser persistibles directamente en columnas Postgres 'timestamp with
    /// time zone'.
    /// </summary>
    public static async Task<(DateTime? Start, DateTime? End)> ComputeAsync(
        AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var startDates = new List<DateTime>();
        var endDates = new List<DateTime>();

        startDates.AddRange(await db.FlightSegments
            .Where(f => f.ReservaId == reservaId && !IsCancelledFlightStatusSql(f.Status))
            .Select(f => f.DepartureTime)
            .ToListAsync(ct));

        // BUG 2 (2026-06-08): ArrivalTime es nullable (vuelos solo de ida). Si no hay hora de llegada,
        // el "fin" del segmento es su salida — mismo patron que el transfer (ReturnDateTime ?? PickupDateTime)
        // de mas abajo. Asi una reserva con un unico vuelo de ida no queda sin EndDate.
        endDates.AddRange(await db.FlightSegments
            .Where(f => f.ReservaId == reservaId && !IsCancelledFlightStatusSql(f.Status))
            .Select(f => f.ArrivalTime ?? f.DepartureTime)
            .ToListAsync(ct));

        startDates.AddRange(await db.HotelBookings
            .Where(h => h.ReservaId == reservaId && !IsCancelledGenericStatusSql(h.Status))
            .Select(h => h.CheckIn)
            .ToListAsync(ct));

        endDates.AddRange(await db.HotelBookings
            .Where(h => h.ReservaId == reservaId && !IsCancelledGenericStatusSql(h.Status))
            .Select(h => h.CheckOut)
            .ToListAsync(ct));

        startDates.AddRange(await db.TransferBookings
            .Where(t => t.ReservaId == reservaId && !IsCancelledGenericStatusSql(t.Status))
            .Select(t => t.PickupDateTime)
            .ToListAsync(ct));

        endDates.AddRange(await db.TransferBookings
            .Where(t => t.ReservaId == reservaId && !IsCancelledGenericStatusSql(t.Status))
            .Select(t => t.ReturnDateTime ?? t.PickupDateTime)
            .ToListAsync(ct));

        startDates.AddRange(await db.PackageBookings
            .Where(p => p.ReservaId == reservaId && !IsCancelledGenericStatusSql(p.Status))
            .Select(p => p.StartDate)
            .ToListAsync(ct));

        // ADR-018: EndDate del paquete puede ser null (ficha "producto-primero"). Se coalesce a
        // StartDate — mismo patron que el transfer (ReturnDateTime ?? PickupDateTime) de mas arriba —
        // para no inventar una fecha de fin ni romper el List<DateTime>.
        endDates.AddRange(await db.PackageBookings
            .Where(p => p.ReservaId == reservaId && !IsCancelledGenericStatusSql(p.Status))
            .Select(p => p.EndDate ?? p.StartDate)
            .ToListAsync(ct));

        // Asistencia (seguro): su vigencia ValidFrom/ValidTo entra al min/max de fechas igual
        // que el check-in/out de hotel. Si faltara aca, una reserva que SOLO tenga asistencia
        // quedaria sin StartDate/EndDate y el lifecycle/los chips de fecha fallarian en silencio.
        startDates.AddRange(await db.AssistanceBookings
            .Where(a => a.ReservaId == reservaId && !IsCancelledGenericStatusSql(a.Status))
            .Select(a => a.ValidFrom)
            .ToListAsync(ct));

        endDates.AddRange(await db.AssistanceBookings
            .Where(a => a.ReservaId == reservaId && !IsCancelledGenericStatusSql(a.Status))
            .Select(a => a.ValidTo)
            .ToListAsync(ct));

        startDates.AddRange(await db.Servicios
            .Where(s => s.ReservaId == reservaId && !IsCancelledGenericStatusSql(s.Status))
            .Select(s => s.DepartureDate)
            .ToListAsync(ct));

        endDates.AddRange(await db.Servicios
            .Where(s => s.ReservaId == reservaId && !IsCancelledGenericStatusSql(s.Status))
            .Select(s => s.ReturnDate ?? s.DepartureDate)
            .ToListAsync(ct));

        DateTime? start = startDates.Count > 0 ? AsUtc(startDates.Min()) : null;
        DateTime? end = endDates.Count > 0 ? AsUtc(endDates.Max()) : null;
        return (start, end);
    }

    /// <summary>
    /// ADR-053 D1.1: predicado CANONICO de "vigente" para los 5 tipos NO-vuelo, escrito INLINE (traducible
    /// a SQL por EF Core — <c>.Trim()</c>/<c>.ToLower()</c>/<c>.StartsWith()</c> se traducen, un helper
    /// C# propio invocado dentro de un <c>Where()</c> NO se traduce). Equivale EXACTO a
    /// <c>WorkflowStatusHelper.MapGenericStatus(status) == WorkflowStatuses.Cancelado</c> — anclado al
    /// INICIO del texto (no <c>Contains</c>): "A confirmar"/"sin emitir"/"desconfirmado" CONTIENEN
    /// variantes de otras palabras pero significan lo CONTRARIO de cancelado. Cubre "Cancelado",
    /// "Cancelada" (femenino), "CANCELADO" (mayusculas) y " Cancelado" (espacio) por igual. Ver el test
    /// <c>WorkflowStatusHelperEquivalenceTests</c> (comparacion de igualdad string por string).
    /// </summary>
    // internal (no private): InternalsVisibleTo("TravelApi.Tests") ya esta configurado en el csproj —
    // permite que WorkflowStatusHelperEquivalenceTests compare este predicado inline, string por string,
    // contra WorkflowStatusHelper.MapGenericStatus/MapFlightStatus (D1.1, D7: si alguien los hace
    // divergir en un refactor futuro, el test lo agarra).
    internal static bool IsCancelledGenericStatusSql(string? status)
        => (status ?? string.Empty).Trim().ToLower().StartsWith("cancel");

    /// <summary>
    /// ADR-053 D1.1: predicado CANONICO de "vigente" para Vuelo — equivale EXACTO a
    /// <c>WorkflowStatusHelper.MapFlightStatus(status) == WorkflowStatuses.Cancelado</c> (codigos IATA
    /// UN/UC/HX/NO, normalizados a mayusculas sin espacios).
    /// </summary>
    internal static bool IsCancelledFlightStatusSql(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToUpper();
        return normalized == "UN" || normalized == "UC" || normalized == "HX" || normalized == "NO";
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>
    /// ADR-053 D1: ESCRITOR UNICO de <c>Reserva.StartDate</c>/<c>EndDate</c>. Reemplaza a
    /// <c>BookingService.RecalculateReservationScheduleAsync</c> (retirado) y a la asignacion directa que
    /// hacian <c>ReservaService.AddServiceAsync</c>/<c>UpdateServiceAsync</c>/<c>RemoveServiceAsync</c> (el
    /// servicio generico) y <c>QuoteService.ConvertToFileCoreAsync</c> — los 4 "agujeros" de escritor
    /// unico que la investigacion de este ADR encontro (T-7 ya roto hoy, no una regla nueva).
    ///
    /// <para><b>Que hace, en orden</b>:</para>
    /// <list type="number">
    ///   <item>Lee <c>Reserva.StartDate</c>/<c>EndDate</c> actuales (para comparar).</item>
    ///   <item>Recalcula con <see cref="ComputeAsync"/> (ya con el predicado que excluye anulados).</item>
    ///   <item>Si CAMBIO: persiste la ventana nueva, apaga <c>NeedsDateRecalculation</c> si estaba
    ///     prendida (D4 — "se apaga sola con cualquier corrida exitosa que mueva la ventana"), y si
    ///     <paramref name="actorUserId"/> no es null escribe el aviso suave efimero
    ///     <c>PendingScheduleWarning</c>/<c>PendingScheduleWarningByUserId</c> (D2.1). Un solo
    ///     <c>SaveChangesAsync</c>.</item>
    ///   <item>Si NO cambio: no toca nada, no hace <c>SaveChangesAsync</c> (evita un roundtrip a la BD
    ///     en el caso mas comun — un servicio que se guarda sin mover la ventana).</item>
    /// </list>
    ///
    /// <para><b>Regla "actor null = sin aviso" (B7)</b>: <paramref name="actorUserId"/> es null en DOS
    /// casos a proposito: (a) el job de reparacion nocturno (<c>AutoRepairTravelingDatesAsync</c>, sin
    /// <c>HttpContext</c> — no hay a quien avisarle), y (b) los borrados duros (<c>Delete*Async</c>/
    /// <c>RemoveServiceAsync</c>, D2 — "no hay '¿esta fecha esta bien?' que confirmar cuando lo que se
    /// hizo fue borrar"). En ambos casos el recalculo y la persistencia de StartDate/EndDate corren
    /// IGUAL; lo unico que se suprime es el aviso.</para>
    ///
    /// <para><b>PR-12 (rastro)</b>: si <paramref name="logger"/> viene informado, se deja un log
    /// estructurado (reservaId, ventana vieja, ventana nueva) cuando la ventana cambio — el "por que" real
    /// (que servicio se edito) ya queda en el AuditLog de la mutacion que disparo este recalculo; este log
    /// es solo trazabilidad tecnica del derivado, no un evento de negocio nuevo.</para>
    /// </summary>
    public static async Task<(DateTime? Start, DateTime? End, bool Changed)> RecalculateAndPersistAsync(
        AppDbContext db,
        int reservaId,
        string? actorUserId,
        string? actorUserName,
        CancellationToken ct = default,
        ILogger? logger = null)
    {
        var reserva = await db.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId, ct);
        if (reserva == null) return (null, null, false);

        var oldStart = reserva.StartDate;
        var oldEnd = reserva.EndDate;

        var (nextStart, nextEnd) = await ComputeAsync(db, reservaId, ct);

        var changed = oldStart != nextStart || oldEnd != nextEnd;
        if (!changed) return (oldStart, oldEnd, false);

        reserva.StartDate = nextStart;
        reserva.EndDate = nextEnd;

        // D4: cualquier corrida EXITOSA del escritor unico que mueve la ventana "cura" la marca de
        // "hace falta recalcular" — no hace falta logica aparte, es gratis por venir del mismo lugar.
        if (reserva.NeedsDateRecalculation)
            reserva.NeedsDateRecalculation = false;

        // D2.1 / B7: el aviso efimero solo se escribe si HAY un actor humano a quien avisarle. Si NO hay
        // actor (borrado duro o job de reparacion), se LIMPIA cualquier pendiente VIEJO en vez de dejarlo
        // como estaba: la ventana acaba de cambiar DE NUEVO, asi que un aviso de un cambio anterior (de
        // otra mutacion) ya esta describiendo algo que dejo de ser cierto. Sin este limpiado, un alta que
        // dejo pendiente "el viaje pasa a terminar el 10/10" seguido de un borrado que recalcula la
        // ventana OTRA VEZ mostraria ese aviso viejo como si fuera del borrado (D2: un borrado NUNCA
        // deja aviso).
        if (actorUserId != null)
        {
            reserva.PendingScheduleWarning = BuildScheduleWarningText(oldStart, oldEnd, nextStart, nextEnd);
            reserva.PendingScheduleWarningByUserId = actorUserId;
        }
        else
        {
            reserva.PendingScheduleWarning = null;
            reserva.PendingScheduleWarningByUserId = null;
        }

        await db.SaveChangesAsync(ct);

        logger?.LogInformation(
            "ADR-053 RecalculateAndPersistAsync: ReservaId={ReservaId} cambio de ventana OldStart={OldStart} " +
            "OldEnd={OldEnd} -> NewStart={NewStart} NewEnd={NewEnd} (actor={HasActor})",
            reservaId, oldStart, oldEnd, nextStart, nextEnd, actorUserId != null);

        return (nextStart, nextEnd, true);
    }

    /// <summary>
    /// D2: texto en criollo del aviso suave, ya listo para pintar (T-13: el front no reconstruye nada).
    /// BORRADOR — el texto final lo fija el gate UX antes de F2 (D2); esta version cubre los 3 casos
    /// (cambio solo de salida, solo de regreso, o ambas) sin asumir que se conoce el tipo de servicio que
    /// disparo el cambio (el escritor unico recalcula desde CERO, no sabe "quien" lo disparo).
    /// </summary>
    private static string BuildScheduleWarningText(
        DateTime? oldStart, DateTime? oldEnd, DateTime? newStart, DateTime? newEnd)
    {
        var startChanged = oldStart != newStart;
        var endChanged = oldEnd != newEnd;

        if (startChanged && endChanged)
        {
            return $"Con este cambio, el viaje pasa a ser del {FormatDate(newStart)} al {FormatDate(newEnd)} " +
                   "— ¿las fechas de los servicios están bien?";
        }

        if (startChanged)
        {
            return $"Con este cambio, el viaje pasa a empezar el {FormatDate(newStart)} " +
                   "— ¿la fecha del servicio está bien?";
        }

        return $"Con este cambio, el viaje pasa a terminar el {FormatDate(newEnd)} " +
               "— ¿la fecha del servicio está bien?";
    }

    private static string FormatDate(DateTime? value)
        => value.HasValue ? value.Value.ToString("dd/MM") : "sin fecha";
}
