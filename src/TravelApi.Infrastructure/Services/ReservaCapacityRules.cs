using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Reglas compartidas de validacion de Reservas para transiciones de estado.
/// Usado por ReservaService (transicion manual) y ReservaLifecycleAutomationService
/// (transicion automatica del job diario), para que las reglas vivan en un solo lugar.
///
/// Reglas implementadas:
/// - Capacidad pasajeros vs servicios (GetBlockReasonAsync).
/// - Estados de servicio: ningun servicio puede estar en "Solicitado" al pasar a
///   Operativo, porque esos no entran al balance del proveedor (ver SupplierService:205)
///   y dejarian la cuenta corriente con datos sucios (GetUnconfirmedServicesBlockReasonAsync).
/// </summary>
public static class ReservaCapacityRules
{
    /// <summary>
    /// Estados de servicio considerados "confirmados" para fines de balance con
    /// proveedor y para pasar la Reserva a Operativo. Refleja la misma lista que
    /// SupplierService usa para computar TotalPurchases.
    /// </summary>
    public static readonly HashSet<string> ConfirmedServiceStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Confirmado", "Emitido", "HK", "TK", "KK", "KL",
        // B2/OBS-1 (2026-06-24): "Finalizado" (servicio prestado al cerrar la reserva) es un servicio
        // RESUELTO/confirmado. Sin esto, tras un revert Closed -> Traveling los guards lo verian como "sin
        // confirmar" (falso positivo) y bloquearian operaciones validas sobre una reserva que ya estuvo cerrada.
        WorkflowStatuses.Finalizado
    };

    /// <summary>
    /// Estados de Reserva en los que NO se permite cargar/modificar servicios con
    /// estado distinto de "confirmado". El motivo: en Operativo/Closed la cuenta
    /// corriente del proveedor ya quedo cerrada en base a los confirmados; meter
    /// un servicio Solicitado nuevo o degradar uno confirmado romperia la coherencia.
    /// </summary>
    public static readonly HashSet<string> ReservaStatusesRequiringConfirmedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        EstadoReserva.Traveling, EstadoReserva.Closed
    };

    /// <summary>
    /// ADR-035 (2026-06-19): PRIMERA COMPUERTA de TODA mutacion de servicios — candado por ESTADO de la
    /// reserva. Cierra la incoherencia de fondo: hoy una reserva Perdida/Cancelada/Esperando-reembolso o
    /// Finalizada dejaba agregar/editar/borrar/cancelar servicios y marcar "Solicitado".
    ///
    /// <para>Modelo coherente del dueño (ADR-036, prepago puro — 3 grupos):</para>
    /// <list type="bullet">
    ///   <item><b>EN ARMADO</b> (Quotation, Budget, InManagement): editar servicios libre.</item>
    ///   <item><b>EN FIRME EDITABLE</b> (Confirmed): editar servicios SOLO con autorizacion viva
    ///     (el candado <c>ReservaLockGuard</c>, que corre DESPUES de esta compuerta).</item>
    ///   <item><b>SOLO LECTURA</b> (Traveling, Closed, Lost, Cancelled, PendingOperatorRefund): servicios SOLO
    ///     LECTURA — bloqueado de raiz. NINGUNA autorizacion lo desbloquea. ADR-036: "En viaje" (Traveling) se
    ///     suma a este grupo (el viaje ya empezo: no se edita ni con autorizacion). ToSettle murio.</item>
    /// </list>
    ///
    /// <para>Decide con la FUENTE UNICA <see cref="ReservaCapabilityPolicy"/> (el mismo
    /// <c>CanEditServices</c> que el frontend usa para apagar botones), asi back y front nunca divergen.
    /// El motivo es texto de estado, sin montos ni costos (respeta el enmascarado see_cost).</para>
    ///
    /// <para><b>Orden de compuertas</b>: esta corre PRIMERO. Recien despues el candado de autorizacion
    /// (<c>ReservaLockGuard</c>) y los guards fiscales (CAE/voucher/recibo), que quedan INTACTOS. La
    /// diferencia: para los CERRADOS no hay autorizacion que valga (hard block); para EN FIRME sigue
    /// valiendo el candado como hoy.</para>
    ///
    /// <para>Reserva inexistente: no-op (deja que el flujo propio devuelva su 404, no inventa bloqueo).</para>
    /// </summary>
    public static async Task EnsureServicesEditableByStateAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default)
    {
        var status = await db.Reservas.AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(ct);

        // Reserva inexistente: no bloqueamos aca; el metodo de negocio devolvera su NotFound.
        if (string.IsNullOrWhiteSpace(status)) return;

        // Misma logica que ve el front (CanEditServices). Construimos un contexto minimo: solo el estado
        // importa para esta capacidad, asi que el resto de los campos van en su valor neutro.
        var capabilities = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            Status: status,
            Balance: 0m,
            HasLiveCae: false,
            HasLiveVoucher: false,
            HasLiveEditAuth: false,
            HasAnyPayment: false));

        if (capabilities.CanEditServices.Allowed) return;

        // Estado de solo lectura: motivo legible segun el grupo terminal (sin datos sensibles).
        throw new InvalidOperationException(ReadOnlyServicesMessageFor(status));
    }

    /// <summary>
    /// G5 (2026-06-24): PRIMERA COMPUERTA de la REPROGRAMACION de viaje (mover la fecha de salida de todo el
    /// itinerario) — candado por ESTADO. Permite reprogramar SOLO desde Confirmada en adelante
    /// ({Confirmed, Traveling}); bloquea pre-venta (Cotizacion/Presupuesto/En gestion) y terminales.
    ///
    /// <para>Es MAS ESTRICTA que <see cref="EnsureServicesEditableByStateAsync"/> a proposito: editar un
    /// servicio suelto se permite en pre-venta, pero "reprogramar el viaje" es una accion operativa de venta
    /// firme. Decide con la FUENTE UNICA <see cref="ReservaCapabilityPolicy"/> (el mismo <c>CanReschedule</c>
    /// que el front usa para apagar el boton). El candado de autorizacion (Confirmed+) y el guard fiscal
    /// (CAE/voucher) se siguen aplicando aparte, DESPUES de esta compuerta.</para>
    ///
    /// <para>Reserva inexistente: no-op (deja que el flujo propio devuelva su 404).</para>
    /// </summary>
    public static async Task EnsureReschedulableByStateAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default)
    {
        var status = await db.Reservas.AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(status)) return;

        var capabilities = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            Status: status,
            Balance: 0m,
            HasLiveCae: false,
            HasLiveVoucher: false,
            HasLiveEditAuth: false,
            HasAnyPayment: false));

        if (capabilities.CanReschedule.Allowed) return;

        // Estado donde no se reprograma: motivo legible (sin datos sensibles). Reusamos el motivo de la
        // capacidad para que front (boton apagado) y back (rechazo) muestren EXACTAMENTE el mismo texto.
        throw new InvalidOperationException(
            capabilities.CanReschedule.Reason ?? ReservaCapabilityPolicy.NotReschedulableStatusReason);
    }

    /// <summary>
    /// ADR-035 (2026-06-19): PRIMERA COMPUERTA de TODA mutacion de PASAJEROS (agregar / completar datos /
    /// cambiar identidad / borrar) — candado por ESTADO de la reserva, MISMO patron que los servicios.
    ///
    /// <para>Cierra la incoherencia detectada: en una reserva CERRADA (Closed/Lost/Cancelled/
    /// PendingOperatorRefund) todavia se podian tocar pasajeros. Ahora es solo lectura DURA: en los terminales
    /// no se puede ni completar un dato faltante, ni agregar, ni borrar un pasajero. NINGUNA autorizacion lo
    /// desbloquea.</para>
    ///
    /// <para>Esto NO cambia la regla de ADR-031 para los estados vivos: en EN ARMADO y EN FIRME, completar un
    /// dato faltante de un pasajero sigue sin pedir autorizacion (lo evalua el servicio aparte). Esta compuerta
    /// SOLO impone el hard block en los terminales — corre PRIMERO, antes del candado de autorizacion y de los
    /// guards fiscales, que quedan intactos.</para>
    ///
    /// <para>Reserva inexistente: no-op (deja que el flujo propio devuelva su 404).</para>
    /// </summary>
    public static Task EnsurePassengersEditableByStateAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default)
        => EnsureEditableByStateAsync(db, reservaId, ReservaEditableArea.Passengers, ct);

    /// <summary>
    /// ADR-035 (2026-06-19): PRIMERA COMPUERTA para editar DATOS DE CABECERA de la reserva (fechas de salida/
    /// regreso y demas datos generales) — candado por ESTADO, MISMO patron que los servicios. En los terminales
    /// es solo lectura dura. El candado de autorizacion (Confirmed+) y los guards fiscales (factura/voucher con
    /// periodo declarado) se siguen aplicando aparte, DESPUES de esta compuerta.
    /// </summary>
    public static Task EnsureReservaDataEditableByStateAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default)
        => EnsureEditableByStateAsync(db, reservaId, ReservaEditableArea.ReservaData, ct);

    /// <summary>
    /// Las tres areas que comparten el MISMO candado por estado (3 grupos del dueño). Se usan para elegir la
    /// capacidad a evaluar y el texto del motivo, sin duplicar la logica de la compuerta.
    /// </summary>
    private enum ReservaEditableArea
    {
        Services,
        Passengers,
        ReservaData
    }

    /// <summary>
    /// Nucleo compartido de la compuerta por estado para servicios, pasajeros y datos de cabecera. Evalua la
    /// FUENTE UNICA <see cref="ReservaCapabilityPolicy"/> (el mismo predicado que apaga botones en el front) y,
    /// si la accion no esta permitida en el estado actual, lanza <see cref="InvalidOperationException"/> (-&gt;
    /// 409) con un motivo legible (sin montos ni costos, respeta el enmascarado see_cost).
    /// </summary>
    private static async Task EnsureEditableByStateAsync(
        AppDbContext db,
        int reservaId,
        ReservaEditableArea area,
        CancellationToken ct)
    {
        var status = await db.Reservas.AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(ct);

        // Reserva inexistente: no bloqueamos aca; el metodo de negocio devolvera su NotFound.
        if (string.IsNullOrWhiteSpace(status)) return;

        // Solo el estado importa para estas capacidades; el resto del contexto va en su valor neutro.
        var capabilities = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            Status: status,
            Balance: 0m,
            HasLiveCae: false,
            HasLiveVoucher: false,
            HasLiveEditAuth: false,
            HasAnyPayment: false));

        var allowed = area switch
        {
            ReservaEditableArea.Services => capabilities.CanEditServices.Allowed,
            ReservaEditableArea.Passengers => capabilities.CanEditPassengers.Allowed,
            ReservaEditableArea.ReservaData => capabilities.CanEditReservaData.Allowed,
            _ => true
        };
        if (allowed) return;

        throw new InvalidOperationException(ReadOnlyMessageFor(status, area));
    }

    /// <summary>
    /// Motivo legible (español, sin montos/costos) para el bloqueo de servicios por estado de solo lectura.
    /// Diferencia el estado para que el vendedor entienda QUE pasa. Cualquier estado no listado cae al mensaje
    /// generico.
    /// </summary>
    private static string ReadOnlyServicesMessageFor(string status)
        => ReadOnlyMessageFor(status, ReservaEditableArea.Services);

    /// <summary>
    /// Motivo legible por estado de solo lectura y por area. El "que" cambia (servicios / pasajeros / datos).
    /// Sin montos ni costos (respeta el enmascarado see_cost).
    ///
    /// <para>ADR-036 (2026-06-21): "En viaje" (Traveling) se suma a los estados de solo lectura (el viaje ya
    /// empezo). Y el mensaje de Finalizada (Closed) YA NO sugiere "reabrir a A liquidar" — ese camino murio
    /// con ToSettle. Para corregir una factura de una reserva finalizada se usa NC/ND, sin reabrir el estado.</para>
    /// </summary>
    private static string ReadOnlyMessageFor(string status, ReservaEditableArea area)
    {
        var what = area switch
        {
            ReservaEditableArea.Passengers => "los pasajeros",
            ReservaEditableArea.ReservaData => "los datos de la reserva",
            _ => "los servicios"
        };

        if (string.Equals(status, EstadoReserva.Traveling, StringComparison.OrdinalIgnoreCase))
            return $"La reserva esta en viaje: {what} son de solo lectura (el viaje ya empezo).";
        if (string.Equals(status, EstadoReserva.Closed, StringComparison.OrdinalIgnoreCase))
            return $"La reserva esta finalizada: {what} son de solo lectura.";
        if (string.Equals(status, EstadoReserva.Cancelled, StringComparison.OrdinalIgnoreCase))
            return $"La reserva esta cancelada: {what} son de solo lectura.";
        if (string.Equals(status, EstadoReserva.Lost, StringComparison.OrdinalIgnoreCase))
            return $"La reserva esta marcada como perdida: {what} son de solo lectura.";
        if (string.Equals(status, EstadoReserva.PendingOperatorRefund, StringComparison.OrdinalIgnoreCase))
            return $"La reserva esta esperando el reembolso del operador: {what} son de solo lectura.";
        return $"En el estado actual de la reserva, {what} son de solo lectura.";
    }

    /// <summary>
    /// Bloquea agregar o modificar un servicio con Status no-confirmado si la
    /// Reserva esta en Operativo o Cerrado. Aplica a Hotel/Transfer/Package/Flight
    /// y al ServicioReserva generico.
    ///
    /// Devuelve mensaje accionable o null si el cambio es coherente.
    /// </summary>
    public static async Task<string?> GetServiceStatusBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        string serviceLabel,
        string? newServiceStatus,
        CancellationToken ct = default)
    {
        var reserva = await db.Reservas.AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => new { r.Status })
            .FirstOrDefaultAsync(ct);
        if (reserva == null) return null;

        if (!ReservaStatusesRequiringConfirmedServices.Contains(reserva.Status))
            return null; // Reserva en Presupuesto/Reservado/Cancelado: no aplica este check

        if (string.IsNullOrWhiteSpace(newServiceStatus))
            return $"El estado del servicio es obligatorio cuando la reserva esta en {reserva.Status}.";

        if (ConfirmedServiceStatuses.Contains(newServiceStatus))
            return null; // OK, esta confirmado

        return $"La reserva esta en estado {reserva.Status}. No se puede cargar/modificar el servicio '{serviceLabel}' " +
               $"con estado '{newServiceStatus}' — debe estar confirmado con el proveedor (alguno de: {string.Join(", ", ConfirmedServiceStatuses)}).";
    }

    /// <summary>
    /// Bloquea cambio de Status confirmado -> no-confirmado si la Reserva tiene
    /// SupplierPayments registrados. Razon: el balance del proveedor ya cuenta esos
    /// pagos contra el servicio confirmado; degradarlo a Solicitado/Cancelado
    /// rompe la coherencia (pagos colgando, balance sucio).
    ///
    /// Granularidad: a nivel Reserva (limitacion del modelo, SupplierPayment no
    /// apunta a HotelBooking/TransferBooking/etc. especifico). Si hay 2 hoteles y
    /// solo se pago uno, igual no podras des-confirmar el otro hasta anular los pagos.
    ///
    /// Devuelve mensaje accionable o null si el cambio es permitido.
    /// </summary>
    public static async Task<string?> GetStatusDowngradeBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        string serviceLabel,
        string? oldServiceStatus,
        string? newServiceStatus,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oldServiceStatus) || string.IsNullOrWhiteSpace(newServiceStatus))
            return null;

        // Solo aplica si: pasaba de confirmado a no-confirmado.
        var wasConfirmed = ConfirmedServiceStatuses.Contains(oldServiceStatus);
        var willBeConfirmed = ConfirmedServiceStatuses.Contains(newServiceStatus);
        if (!wasConfirmed || willBeConfirmed) return null;

        var totalPaid = await db.SupplierPayments.AsNoTracking()
            .Where(p => p.ReservaId == reservaId)
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        if (totalPaid <= 0m) return null;

        return $"No se puede degradar el estado del servicio '{serviceLabel}' (de '{oldServiceStatus}' a '{newServiceStatus}'): " +
               $"esta reserva tiene pagos al proveedor registrados por ${totalPaid:N2}. " +
               "Anula los pagos al proveedor antes de des-confirmar.";
    }

    /// <summary>
    /// Devuelve un mensaje de bloqueo si hay inconsistencia entre cantidad de pasajeros
    /// nominales y capacidad de los servicios cargados. Si todo coherente, devuelve null.
    /// Independiente del estado financiero (deuda).
    ///
    /// Chequeos:
    /// 1) Total: Passengers.Count > max capacidad de hoteles/transfers/packages.
    /// 2) Por servicio (si hay assignments en PassengerServiceAssignments):
    ///    assignments por servicio > capacidad de ese servicio.
    /// </summary>
    public static async Task<string?> GetBlockReasonAsync(AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var reserva = await db.Reservas
            .AsNoTracking()
            .Include(r => r.Passengers)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .FirstOrDefaultAsync(r => r.Id == reservaId, ct);
        if (reserva == null) return null;

        var paxCount = reserva.Passengers?.Count ?? 0;
        if (paxCount == 0) return null; // sin pasajeros no hay como exceder

        // Chequeo 1: total
        var hotelCap = reserva.HotelBookings?.Sum(h => h.GetExpectedPaxCount()) ?? 0;
        var transferCap = reserva.TransferBookings?.Max(t => (int?)t.GetExpectedPaxCount()) ?? 0;
        var packageCap = reserva.PackageBookings?.Sum(p => p.GetExpectedPaxCount()) ?? 0;
        var assistanceCap = reserva.AssistanceBookings?.Sum(a => a.GetExpectedPaxCount()) ?? 0;
        var maxExpected = Math.Max(hotelCap, Math.Max(transferCap, Math.Max(packageCap, assistanceCap)));

        if (maxExpected > 0 && paxCount > maxExpected)
        {
            return $"Hay {paxCount} pasajeros cargados pero los servicios solo soportan {maxExpected}. " +
                   "Ajusta la capacidad de los servicios o eliminá pasajeros antes de pasar a Operativo.";
        }

        // Chequeo 2: por asignacion individual
        var passengerIds = reserva.Passengers!.Select(p => p.Id).ToList();
        var assignments = await db.PassengerServiceAssignments
            .AsNoTracking()
            .Where(a => passengerIds.Contains(a.PassengerId))
            .Select(a => new { a.ServiceType, a.ServiceId })
            .ToListAsync(ct);

        if (assignments.Count == 0) return null;

        var assignmentCounts = assignments
            .GroupBy(a => new { a.ServiceType, a.ServiceId })
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var hotel in reserva.HotelBookings ?? Enumerable.Empty<HotelBooking>())
        {
            var key = new { ServiceType = AssignmentServiceType.Hotel, ServiceId = hotel.Id };
            if (assignmentCounts.TryGetValue(key, out var count))
            {
                var cap = hotel.GetExpectedPaxCount();
                if (cap > 0 && count > cap)
                {
                    return $"El hotel '{hotel.HotelName ?? "Hotel"}' tiene {count} pasajeros asignados pero su capacidad es {cap}. " +
                           "Ajusta la capacidad o quita asignaciones antes de pasar a Operativo.";
                }
            }
        }

        foreach (var transfer in reserva.TransferBookings ?? Enumerable.Empty<TransferBooking>())
        {
            var key = new { ServiceType = AssignmentServiceType.Transfer, ServiceId = transfer.Id };
            if (assignmentCounts.TryGetValue(key, out var count))
            {
                var cap = transfer.GetExpectedPaxCount();
                if (cap > 0 && count > cap)
                {
                    return $"El transfer ({transfer.VehicleType ?? "vehiculo"}) tiene {count} pasajeros asignados pero su capacidad es {cap}. " +
                           "Ajusta la capacidad o quita asignaciones antes de pasar a Operativo.";
                }
            }
        }

        foreach (var package in reserva.PackageBookings ?? Enumerable.Empty<PackageBooking>())
        {
            var key = new { ServiceType = AssignmentServiceType.Package, ServiceId = package.Id };
            if (assignmentCounts.TryGetValue(key, out var count))
            {
                var cap = package.GetExpectedPaxCount();
                if (cap > 0 && count > cap)
                {
                    return $"El paquete '{package.PackageName ?? "Paquete"}' tiene {count} pasajeros asignados pero su capacidad es {cap}. " +
                           "Ajusta la capacidad o quita asignaciones antes de pasar a Operativo.";
                }
            }
        }

        foreach (var assistance in reserva.AssistanceBookings ?? Enumerable.Empty<AssistanceBooking>())
        {
            var key = new { ServiceType = AssignmentServiceType.Assistance, ServiceId = assistance.Id };
            if (assignmentCounts.TryGetValue(key, out var count))
            {
                var cap = assistance.GetExpectedPaxCount();
                if (cap > 0 && count > cap)
                {
                    return $"La asistencia '{assistance.PlanType ?? "seguro"}' tiene {count} pasajeros asignados pero su capacidad es {cap}. " +
                           "Ajusta la capacidad o quita asignaciones antes de pasar a Operativo.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// En reservas en estado Presupuesto, los servicios siempre deben estar en
    /// "Solicitado" — todavia no se confirman con el proveedor (no es una reserva real).
    /// Devuelve true si hay que forzar el status a Solicitado (segun el estado actual
    /// de la reserva).
    /// </summary>
    public static async Task<bool> ShouldForceSolicitadoStatusAsync(AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var reservaStatus = await db.Reservas.AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(ct);
        return string.Equals(reservaStatus, EstadoReserva.Budget, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ADR-036 (2026-06-22): true si la reserva tiene AL MENOS un servicio cargado de CUALQUIER tipo.
    /// "Servicio" = vuelo, hotel, transfer, paquete, asistencia o servicio generico (la coleccion Servicios).
    /// Una reserva de solo ida tiene al menos el aereo, asi que NO cuenta como vacia.
    ///
    /// <para>Esta es la fuente unica de la definicion de "reserva vacia" para el lifecycle: la usa el job
    /// para (A) no promover a "En viaje" una reserva sin servicios y (B) sanear las que ya quedaron
    /// atascadas en "En viaje" vacias. Mismo conjunto de colecciones que computa
    /// <see cref="ReservaScheduleCalculator"/> para las fechas.</para>
    ///
    /// <para><b>DECISION CONSCIENTE — cuenta servicios SIN mirar su estado</b>: un servicio cancelado o
    /// anulado SIGUE contando como "tiene servicios" (la query solo mira existencia de fila, no
    /// <c>Status</c>). Es deliberado: mantiene la coherencia fechas&lt;-&gt;emptiness con
    /// <see cref="ReservaScheduleCalculator"/> (que tambien computa las fechas sobre todas las filas, sin
    /// filtrar por estado). Asi "una reserva sin fechas" y "una reserva vacia" significan lo mismo. NO cambiar
    /// a filtrar por estado sin revisar tambien el calculo de fechas, o ambos quedarian descalibrados.</para>
    ///
    /// <para>Corta apenas encuentra el primer servicio (||): la mayoria de las reservas reales tienen
    /// algun servicio, asi que normalmente alcanza con la primera o segunda consulta.</para>
    /// </summary>
    public static async Task<bool> HasAnyServiceAsync(AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        return await db.FlightSegments.AnyAsync(f => f.ReservaId == reservaId, ct)
            || await db.HotelBookings.AnyAsync(h => h.ReservaId == reservaId, ct)
            || await db.TransferBookings.AnyAsync(t => t.ReservaId == reservaId, ct)
            || await db.PackageBookings.AnyAsync(p => p.ReservaId == reservaId, ct)
            || await db.AssistanceBookings.AnyAsync(a => a.ReservaId == reservaId, ct)
            || await db.Servicios.AnyAsync(s => s.ReservaId == reservaId, ct);
    }

    /// <summary>
    /// Devuelve la capacidad esperada de un servicio especifico (Hotel/Transfer/Package).
    /// Devuelve null si el tipo no declara capacidad (Flight/Generic).
    /// Devuelve 0 si la entidad no fue encontrada (caller debe manejar).
    /// </summary>
    public static async Task<int?> GetServiceCapacityAsync(AppDbContext db, string serviceType, int serviceId, CancellationToken ct = default)
    {
        return serviceType switch
        {
            AssignmentServiceType.Hotel => await db.HotelBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => (int?)(b.Adults + b.Children))
                .FirstOrDefaultAsync(ct),
            AssignmentServiceType.Transfer => await db.TransferBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => (int?)b.Passengers)
                .FirstOrDefaultAsync(ct),
            AssignmentServiceType.Package => await db.PackageBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => (int?)(b.Adults + b.Children))
                .FirstOrDefaultAsync(ct),
            AssignmentServiceType.Assistance => await db.AssistanceBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => (int?)(b.Adults + b.Children))
                .FirstOrDefaultAsync(ct),
            // Flight y Generic no declaran capacidad — devolvemos null para que el caller no bloquee.
            _ => null
        };
    }

    /// <summary>
    /// Bloquea asignar un pasajero NUEVO a un servicio que ya esta lleno.
    /// "Lleno" = count(assignments existentes para ese servicio) >= capacidad declarada.
    /// Si capacidad es 0 o null (no declarada), no bloquea — permite asignar libremente.
    ///
    /// IMPORTANTE: el caller debe garantizar que el pasajero NO esta ya asignado al
    /// servicio (es decir, llamar despues del check de idempotencia). Sino bloquea
    /// re-asignaciones que deberian ser no-op.
    ///
    /// Devuelve mensaje accionable o null si esta permitido.
    /// </summary>
    public static async Task<string?> GetServiceFullBlockReasonAsync(
        AppDbContext db,
        string serviceType,
        int serviceId,
        string serviceLabel,
        CancellationToken ct = default)
    {
        var cap = await GetServiceCapacityAsync(db, serviceType, serviceId, ct);
        if (cap is null or <= 0) return null; // sin capacidad declarada → no bloqueo

        var currentCount = await db.PassengerServiceAssignments.AsNoTracking()
            .CountAsync(a => a.ServiceType == serviceType && a.ServiceId == serviceId, ct);
        if (currentCount < cap.Value) return null;

        return $"El servicio '{serviceLabel}' ya tiene {currentCount} pasajero(s) asignado(s) y su capacidad es {cap.Value}. " +
               "Ampliá la capacidad del servicio o quitá una asignación existente antes de agregar uno nuevo.";
    }

    /// <summary>
    /// Bloquea pase a Operativo si algun servicio no esta en estado "Confirmado" (o
    /// equivalente). Razon: servicios no confirmados NO entran al balance del
    /// proveedor (SupplierService:205) y su confirmacion posterior haria que el
    /// balance pegue un salto retroactivo. Ademas, operativamente no deberian
    /// ejecutarse servicios no confirmados con el proveedor.
    ///
    /// Devuelve mensaje accionable o null si todos los servicios estan confirmados.
    /// </summary>
    public static async Task<string?> GetUnconfirmedServicesBlockReasonAsync(AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var unconfirmed = new List<string>();

        var hotels = await db.HotelBookings.AsNoTracking()
            .Where(b => b.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(b.Status))
            .Select(b => new { b.HotelName, b.Status })
            .ToListAsync(ct);
        foreach (var h in hotels)
            unconfirmed.Add($"Hotel '{h.HotelName ?? "sin nombre"}' ({h.Status})");

        var transfers = await db.TransferBookings.AsNoTracking()
            .Where(b => b.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(b.Status))
            .Select(b => new { b.VehicleType, b.Status })
            .ToListAsync(ct);
        foreach (var t in transfers)
            unconfirmed.Add($"Transfer {t.VehicleType ?? ""} ({t.Status})".Trim());

        var packages = await db.PackageBookings.AsNoTracking()
            .Where(b => b.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(b.Status))
            .Select(b => new { b.PackageName, b.Status })
            .ToListAsync(ct);
        foreach (var p in packages)
            unconfirmed.Add($"Paquete '{p.PackageName ?? "sin nombre"}' ({p.Status})");

        var flights = await db.FlightSegments.AsNoTracking()
            .Where(f => f.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(f.Status))
            // ADR-018: traemos ProductName para que la identidad caiga en el si la ficha "producto-primero"
            // no cargo aerolinea/numero (sino el mensaje mostraria "Vuelo " vacio).
            .Select(f => new { f.ProductName, f.AirlineCode, f.FlightNumber, f.Status })
            .ToListAsync(ct);
        foreach (var f in flights)
            unconfirmed.Add($"Vuelo {ServiceDisplayName.ForFlight(f.ProductName, f.AirlineCode, f.FlightNumber)} ({f.Status})");

        var assistances = await db.AssistanceBookings.AsNoTracking()
            .Where(b => b.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(b.Status))
            .Select(b => new { b.PlanType, b.Status })
            .ToListAsync(ct);
        foreach (var a in assistances)
            unconfirmed.Add($"Asistencia '{a.PlanType ?? "seguro"}' ({a.Status})");

        var generics = await db.Servicios.AsNoTracking()
            .Where(s => s.ReservaId == reservaId && !ConfirmedServiceStatuses.Contains(s.Status))
            .Select(s => new { s.Description, s.Status })
            .ToListAsync(ct);
        foreach (var g in generics)
            unconfirmed.Add($"Servicio '{g.Description ?? "sin descripcion"}' ({g.Status})");

        if (unconfirmed.Count == 0) return null;

        var detail = string.Join(", ", unconfirmed.Take(5));
        var more = unconfirmed.Count > 5 ? $" y {unconfirmed.Count - 5} mas" : "";
        return $"Hay servicio(s) sin confirmar con el proveedor: {detail}{more}. " +
               "Confirma todos los servicios antes de pasar a Operativo (los servicios no confirmados no entran al balance del proveedor).";
    }
}
