using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// REPROGRAMAR VIAJE (2026-06-23): mover JUNTAS todas las fechas de todos los servicios de una reserva por
/// un desplazamiento de N dias. Es lo que hace un operador cuando "corre" el viaje completo (ej. el vuelo
/// se atraso una semana y todo el itinerario se mueve con el), sin tener que editar servicio por servicio.
///
/// <para><b>Por que vive aca y no en cada Update*</b>: el desplazamiento es UNA sola operacion atomica sobre
/// la reserva entera. Reusa los MISMOS guards que la edicion de un servicio individual (candado por estado,
/// candado de autorizacion en Confirmed, guard fiscal CAE/voucher), pero los corre UNA vez por reserva, no
/// por servicio. NO pasa por los Update*Async (que ademas tocan precio/proveedor/estado): reprogramar SOLO
/// mueve fechas.</para>
/// </summary>
public partial class BookingService
{
    /// <inheritdoc />
    public async Task<RescheduleReservaResult> RescheduleAsync(
        string reservaPublicIdOrLegacyId, RescheduleReservaRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);

        var reserva = await _db.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId, ct);
        if (reserva == null) throw new KeyNotFoundException("Reserva no encontrada");

        // 1) Resolver el desplazamiento en dias enteros (modo por shift directo o derivado de nueva salida).
        //    Se valida ANTES de tocar guards: un request mal formado (los dos modos, o ninguno) es 400, no 409.
        var daysShift = ResolveDaysShift(req, reserva);

        // 2) MISMOS guards que la edicion de un servicio individual, en el MISMO orden:
        //    (a) candado por ESTADO: terminal/solo-lectura (En viaje/Cancelada/Perdida/Finalizada/Esperando
        //        reembolso) bloquea de raiz, ANTES de cualquier otra compuerta. Reusa CanEditServices: reprogramar
        //        ES editar las fechas de los servicios.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        //    (b) candado de AUTORIZACION: en Confirmada exige autorizacion viva (si no, 409). Registra la
        //        operacion como ReservaDataEdited (es una mutacion de cabecera que afecta a todos los servicios).
        await GuardReservaLockAsync(
            reservaId,
            ReservaEditAuthorizationOperations.ReservaDataEdited,
            entityType: AuditActions.ReservaEntityName,
            entityId: reservaId,
            summary: $"Reprogramacion del viaje: {daysShift:+#;-#;0} dia(s)",
            ct);
        //    (c) guard FISCAL: si hay factura con CAE vivo o voucher emitido, NO se reprograma por aca (va por
        //        anulacion/reemision). Mismo mensaje y misma fuente que la edicion de fechas de la reserva (CODE-03).
        var fiscalBlockReason = await Reservations.MutationGuards
            .GetReservaDatesMutationBlockReasonAsync(_db, reservaId, ct);
        if (fiscalBlockReason != null)
        {
            _logger.LogWarning(
                "RescheduleAsync rejected (fiscal). ReservaId={ReservaId}. Reason={Reason}",
                reservaId, fiscalBlockReason);
            throw new InvalidOperationException(fiscalBlockReason);
        }

        // 3) Aplicar el shift a TODAS las fechas de TODOS los servicios, en una sola unidad de trabajo.
        //    daysShift == 0 es un no-op valido: no movemos nada, solo devolvemos las fechas actuales.
        var servicesMoved = daysShift == 0
            ? 0
            : await ShiftAllServiceDatesAsync(reservaId, daysShift, ct);

        // 4) Persistir el desplazamiento + el rastro de auditoria en UN solo SaveChanges (atomico: o se mueve
        //    todo y queda auditado, o no se mueve nada). El audit se STAGEA para entrar en la misma transaccion.
        if (daysShift != 0)
        {
            StageRescheduleAudit(reservaId, daysShift, servicesMoved);
            await _db.SaveChangesAsync(ct);
        }

        // 5) Recalcular StartDate/EndDate de la reserva desde el nuevo min/max de fechas (fuente unica).
        //    Hace su propio SaveChanges si cambio algo.
        await RecalculateReservationScheduleAsync(reservaId, ct);

        // Releer las fechas de cabecera ya recalculadas para devolverselas al front.
        var (newStart, newEnd) = await ReservaScheduleCalculator.ComputeAsync(_db, reservaId, ct);

        return new RescheduleReservaResult
        {
            DaysShift = daysShift,
            ServicesMoved = servicesMoved,
            NewStartDate = newStart,
            NewEndDate = newEnd,
        };
    }

    /// <summary>
    /// Resuelve el desplazamiento en DIAS ENTEROS a partir del request. Acepta exactamente UN modo:
    /// <list type="bullet">
    ///   <item><c>DaysShift</c> directo (+/-, puede ser 0 = no-op).</item>
    ///   <item><c>NewStartDate</c>: deriva shift = (NewStartDate - StartDate actual), redondeado a dias enteros.
    ///     Requiere que la reserva ya tenga StartDate (si no, no hay desde donde derivar).</item>
    /// </list>
    /// Lanza <see cref="ArgumentException"/> (-&gt; 400) si vienen los dos modos, ninguno, o si NewStartDate
    /// no se puede derivar.
    /// </summary>
    private static int ResolveDaysShift(RescheduleReservaRequest req, Reserva reserva)
    {
        var hasShift = req.DaysShift.HasValue;
        var hasNewStart = req.NewStartDate.HasValue;

        if (hasShift && hasNewStart)
            throw new ArgumentException("Enviá un solo modo: desplazamiento en días O nueva fecha de salida, no ambos.");
        if (!hasShift && !hasNewStart)
            throw new ArgumentException("Indicá el desplazamiento en días o una nueva fecha de salida.");

        if (hasShift)
            return req.DaysShift!.Value;

        // Modo "nueva fecha de salida": derivamos el shift contra la salida actual de la reserva.
        if (reserva.StartDate is null)
            throw new ArgumentException(
                "La reserva no tiene una fecha de salida desde la cual mover. Usá el desplazamiento en días.");

        // Comparamos por fecha-calendario (.Date) para que el shift sea un numero entero de dias y no se
        // contamine con la hora de pared de los servicios (un vuelo a las 14:30 no debe dar 6 dias y "pico").
        var currentStartDate = reserva.StartDate.Value.Date;
        var requestedStartDate = req.NewStartDate!.Value.Date;
        return (int)(requestedStartDate - currentStartDate).TotalDays;
    }

    /// <summary>
    /// Desplaza por <paramref name="daysShift"/> dias TODAS las fechas de TODOS los servicios de la reserva,
    /// de los 6 tipos (vuelo, hotel, transfer, paquete, asistencia, generico). Las fechas nullables solo se
    /// mueven si tienen valor (null queda null). Devuelve la cantidad de servicios desplazados.
    ///
    /// <para><b>DECISION — se mueven TAMBIEN los servicios cancelados/anulados</b>: el calculo de fechas de la
    /// reserva (<see cref="ReservaScheduleCalculator"/>) incluye a proposito los servicios cancelados (ADR-019
    /// R8, alimenta el StartDate persistido que mueve el lifecycle). Si reprogramaramos solo los vivos, al
    /// recalcular StartDate/EndDate (que mira TODAS las filas) la cabecera no reflejaria limpiamente el shift, y
    /// un re-shift no seria simetrico. Mover el itinerario completo (vivos + cancelados) mantiene coherente la
    /// relacion fechas&lt;-&gt;reserva y hace la operacion idempotente al revertir (+N y luego -N vuelve al origen).
    /// La plata NO cambia: las fechas no entran en el costo ni en el saldo, asi que mover un servicio cancelado
    /// solo corrige su fecha historica, no reactiva nada.</para>
    ///
    /// <para><b>Kind de las fechas</b>: <see cref="DateTime.AddDays"/> preserva el <see cref="DateTimeKind"/>.
    /// Todas las fechas se persisten con Kind=Utc ("fecha de pared disfrazada de Utc", ver
    /// NormalizeAirportWallClock/NormalizeCalendarDate), asi que sumar dias enteros mantiene el contrato sin
    /// re-normalizar ni correr la hora. Mover por dias ENTEROS no cruza husos ni DST (no son instantes reales).</para>
    /// </summary>
    private async Task<int> ShiftAllServiceDatesAsync(int reservaId, int daysShift, CancellationToken ct)
    {
        var moved = 0;

        // Vuelo: DepartureTime (no nullable) + ArrivalTime (nullable, solo si hay hora de llegada).
        var flights = await _db.FlightSegments.Where(f => f.ReservaId == reservaId).ToListAsync(ct);
        foreach (var flight in flights)
        {
            flight.DepartureTime = flight.DepartureTime.AddDays(daysShift);
            if (flight.ArrivalTime.HasValue)
                flight.ArrivalTime = flight.ArrivalTime.Value.AddDays(daysShift);
            moved++;
        }

        // Hotel: CheckIn / CheckOut (ambas no nullable).
        var hotels = await _db.HotelBookings.Where(h => h.ReservaId == reservaId).ToListAsync(ct);
        foreach (var hotel in hotels)
        {
            hotel.CheckIn = hotel.CheckIn.AddDays(daysShift);
            hotel.CheckOut = hotel.CheckOut.AddDays(daysShift);
            moved++;
        }

        // Transfer: PickupDateTime (no nullable) + ReturnDateTime (nullable, solo ida y vuelta).
        var transfers = await _db.TransferBookings.Where(t => t.ReservaId == reservaId).ToListAsync(ct);
        foreach (var transfer in transfers)
        {
            transfer.PickupDateTime = transfer.PickupDateTime.AddDays(daysShift);
            if (transfer.ReturnDateTime.HasValue)
                transfer.ReturnDateTime = transfer.ReturnDateTime.Value.AddDays(daysShift);
            moved++;
        }

        // Paquete: StartDate (no nullable) + EndDate (nullable, la ficha "producto-primero" permite omitirla).
        var packages = await _db.PackageBookings.Where(p => p.ReservaId == reservaId).ToListAsync(ct);
        foreach (var package in packages)
        {
            package.StartDate = package.StartDate.AddDays(daysShift);
            if (package.EndDate.HasValue)
                package.EndDate = package.EndDate.Value.AddDays(daysShift);
            moved++;
        }

        // Asistencia (seguro): ValidFrom / ValidTo (ambas no nullable).
        var assistances = await _db.AssistanceBookings.Where(a => a.ReservaId == reservaId).ToListAsync(ct);
        foreach (var assistance in assistances)
        {
            assistance.ValidFrom = assistance.ValidFrom.AddDays(daysShift);
            assistance.ValidTo = assistance.ValidTo.AddDays(daysShift);
            moved++;
        }

        // Generico: DepartureDate (no nullable) + ReturnDate (nullable).
        var generics = await _db.Servicios.Where(s => s.ReservaId == reservaId).ToListAsync(ct);
        foreach (var generic in generics)
        {
            generic.DepartureDate = generic.DepartureDate.AddDays(daysShift);
            if (generic.ReturnDate.HasValue)
                generic.ReturnDate = generic.ReturnDate.Value.AddDays(daysShift);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Deja STAGEADO (sin guardar) el evento de auditoria de la reprogramacion, para que entre en el MISMO
    /// SaveChanges que el desplazamiento de fechas (atomico). El detail JSON lleva el shift, el conteo de
    /// servicios movidos y la reserva — NUNCA montos ni datos sensibles. Si no hay IAuditService inyectado
    /// (tests con el ctor corto), la operacion igual ocurre; solo no se registra el evento.
    /// </summary>
    private void StageRescheduleAudit(int reservaId, int daysShift, int servicesMoved)
    {
        if (_auditService is null) return;

        var (userId, userName) = GetActor();
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            reservaId,
            daysShift,
            servicesMoved,
        });
        _auditService.StageBusinessEvent(
            AuditActions.ReservaRescheduled,
            AuditActions.ReservaEntityName,
            reservaId.ToString(),
            details,
            userId ?? string.Empty,
            userName);
    }
}
