using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// Reglas compartidas que decidan si una entidad relacionada con una Reserva
/// puede ser borrada. Cada metodo devuelve el motivo de bloqueo (mensaje
/// accionable en espanol) o <c>null</c> si la operacion esta permitida.
///
/// Patron calcado de <see cref="ReservaCapacityRules"/>: la regla de negocio
/// vive en un solo lugar y se reusa desde ReservaService, BookingService y
/// PaymentService. Cualquier path nuevo de borrado debe consultarlas tambien.
///
/// Tickets relacionados:
///  - C25 (reserva delete acotada a Budget).
///  - C26 (delete de servicios solo en Budget).
///  - C27 (delete de pasajero bloqueado por factura emitida).
///  - C28 (delete de pago bloqueado por recibo o factura).
/// </summary>
public static class DeleteGuards
{
    /// <summary>
    /// Devuelve el motivo de bloqueo para borrar una Reserva o null si esta permitido.
    /// Reglas (C25): solo se puede borrar fisicamente si la reserva esta en Budget
    /// y NO tiene pagos vivos, vouchers emitidos, ni facturas con CAE.
    /// </summary>
    public static async Task<string?> GetReservaDeleteBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default)
    {
        var reserva = await db.Reservas.AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => new { r.Status })
            .FirstOrDefaultAsync(ct);
        if (reserva == null) return null; // caller decide si devolver 404; aca no es nuestro problema

        // ADR-020 (C25 extendido): una reserva se borra fisicamente en las etapas comerciales
        // tempranas (Cotizacion o Presupuesto). Mas alla de ahi se cancela/archiva, no se borra.
        var isEarlyStage =
            string.Equals(reserva.Status, EstadoReserva.Quotation, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reserva.Status, EstadoReserva.Budget, StringComparison.OrdinalIgnoreCase);
        if (!isEarlyStage)
        {
            return $"Solo se pueden eliminar reservas en Cotizacion o Presupuesto. " +
                   $"Esta reserva esta en estado '{reserva.Status}'. Para reservas en otro estado, cancelala o archivala.";
        }

        var hasLivePayments = await db.Payments.AnyAsync(p => p.ReservaId == reservaId && !p.IsDeleted, ct);
        if (hasLivePayments)
            return "No se puede eliminar una Reserva con pagos registrados. Eliminá los pagos primero.";

        var hasIssuedVoucher = await db.Vouchers.AnyAsync(v => v.ReservaId == reservaId && v.Status == "Issued", ct);
        if (hasIssuedVoucher)
            return "No se puede eliminar una Reserva con vouchers emitidos. Anulá los vouchers primero o cambiá el estado a Cancelado.";

        var hasInvoiceWithCae = await db.Invoices.AnyAsync(i => i.ReservaId == reservaId && !string.IsNullOrEmpty(i.CAE), ct);
        if (hasInvoiceWithCae)
            return "No se puede eliminar una Reserva con facturas AFIP emitidas (CAE asignado). Marcá la Reserva como Cancelada.";

        // INV-020-04: aunque la reserva este en Cotizacion/Presupuesto, NO se borra fisicamente si
        // algun servicio ya fue confirmado por el operador (ConfirmedAt sellado = compromiso/deuda
        // con el proveedor). El borrado fisico cascadea y tiraria ese servicio sin pasar por la
        // cancelacion que liquida la penalidad/deuda. Hay que CANCELAR esos servicios primero.
        // Usamos ConfirmedAt != null como marca (mismo criterio que el guard de borrado de servicio,
        // GetServiceDeleteBlockReasonAsync): una vez sellada, queda como historia aunque el servicio
        // se re-solicite, lo que mantiene el bloqueo del lado seguro.
        if (await ReservaHasOperatorConfirmedServiceAsync(db, reservaId, ct))
        {
            return "No se puede eliminar la Reserva porque tiene servicios confirmados con el operador " +
                   "(hay compromiso o deuda con el proveedor). Cancelá esos servicios primero.";
        }

        return null;
    }

    /// <summary>
    /// ADR-020 (INV-020-04): indica si la reserva tiene AL MENOS un servicio confirmado por el
    /// operador (ConfirmedAt sellado) en cualquiera de las 6 colecciones de servicios. Se cortocircuita
    /// en la primera coincidencia para no recorrer todas las tablas si ya encontro una.
    ///
    /// <para>Es PUBLICO a proposito: la capacidad <c>canDelete</c> (ReservaService -&gt; ReservaCapabilityPolicy)
    /// debe coincidir EXACTO con lo que este guard bloquea, para que el front no muestre "Eliminar" en un
    /// presupuesto cuyo borrado el guard rechazaria por tener un servicio confirmado con el operador. Una sola
    /// fuente de verdad evita que capacidad y guard divergan.</para>
    /// </summary>
    public static async Task<bool> ReservaHasOperatorConfirmedServiceAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct)
    {
        if (await db.HotelBookings.AnyAsync(h => h.ReservaId == reservaId && h.ConfirmedAt != null, ct)) return true;
        if (await db.FlightSegments.AnyAsync(f => f.ReservaId == reservaId && f.ConfirmedAt != null, ct)) return true;
        if (await db.TransferBookings.AnyAsync(t => t.ReservaId == reservaId && t.ConfirmedAt != null, ct)) return true;
        if (await db.PackageBookings.AnyAsync(p => p.ReservaId == reservaId && p.ConfirmedAt != null, ct)) return true;
        if (await db.AssistanceBookings.AnyAsync(a => a.ReservaId == reservaId && a.ConfirmedAt != null, ct)) return true;
        if (await db.Servicios.AnyAsync(s => s.ReservaId == reservaId && s.ConfirmedAt != null, ct)) return true;
        return false;
    }

    /// <summary>
    /// ADR-020 (F5): devuelve el motivo de bloqueo para BORRAR un servicio, o null si esta permitido.
    /// Manda EL SERVICIO, no el estado de la reserva (el viejo guard C26 reserva-level MURIO — era el
    /// que tiraba "la reserva esta en estado 'Sold'" y disparo este rediseño).
    ///
    /// <para>Reglas:</para>
    /// <list type="number">
    /// <item>Si el servicio fue confirmado por el operador (<paramref name="serviceIsOperatorConfirmed"/>
    ///   = ConfirmedAt != null O IsOperatorConfirmed O IsResolved) -> NO se borra: hay que CANCELARLO
    ///   (queda tachado y su monto se resta del saldo). Un aereo HK sin ticket entra aca: el PNR ya es
    ///   compromiso con el consolidador, aunque no resuelva el file.</item>
    /// <item>Voucher emitido de la reserva -> anular primero.</item>
    /// <item>Pago soft-deleted vinculado a ESTE servicio generico via ServicioReservaId -> riesgo de
    ///   hard-delete en cascada (se preserva).</item>
    /// </list>
    ///
    /// <para>El bloqueo generico "la reserva tiene pagos vivos" YA NO aplica al borrado de un servicio
    /// nunca-confirmado (los pagos son de la reserva, no del servicio; el recalculo de saldo lo absorbe).
    /// Si la reserva esta bajo candado (Confirmada en adelante), el candado (F4) exige autorizacion ANTES
    /// de llegar a este guard — es ortogonal a esta regla.</para>
    /// </summary>
    public static async Task<string?> GetServiceDeleteBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        bool serviceIsOperatorConfirmed,
        int? genericServiceId = null,
        CancellationToken ct = default,
        ILogger? logger = null)
    {
        if (reservaId == 0) return null; // servicios sin reserva (legacy) no aplican

        // (1) Confirmado con el operador -> solo se cancela, no se borra.
        if (serviceIsOperatorConfirmed)
        {
            return "No se puede borrar un servicio ya confirmado con el operador. Cancelalo " +
                   "(queda tachado, con quien y cuando, y su monto se resta del saldo del cliente).";
        }

        // (2) Voucher emitido de la reserva.
        var hasIssuedVoucher = await db.Vouchers.AnyAsync(v => v.ReservaId == reservaId && v.Status == "Issued", ct);
        if (hasIssuedVoucher)
            return "No se pueden eliminar servicios de una reserva con vouchers ya emitidos. Anulá los vouchers primero.";

        // (3) Pago soft-deleted vinculado a ESTE servicio generico (cascade hard-delete = riesgo fiscal).
        // Solo el ServicioReserva generico tiene el link Payment.ServicioReservaId; los tipados no.
        if (genericServiceId.HasValue)
        {
            var hasSoftDeletedPaymentLinked = await db.Payments
                .IgnoreQueryFilters()
                .AnyAsync(p => p.IsDeleted && p.ServicioReservaId == genericServiceId.Value, ct);
            if (hasSoftDeletedPaymentLinked)
            {
                logger?.LogWarning(
                    "Service delete blocked: soft-deleted payment(s) linked to generic service via ServicioReservaId. " +
                    "ReservaId={ReservaId} ServiceId={ServiceId}. Cascade hard-delete riesgo fiscal.",
                    reservaId, genericServiceId.Value);
                return "No se puede eliminar el servicio porque tiene pagos vinculados (incluso eliminados). " +
                       "Restaurá o reasigná los pagos primero.";
            }
        }

        return null;
    }

    /// <summary>
    /// Reglas pre-existentes consolidadas: no se puede borrar servicios si hay
    /// pagos vivos del cliente o vouchers emitidos. Reusable de forma aislada
    /// para no acoplar el guard de estado (C26) con los guards historicos —
    /// algunos call sites pueden necesitar uno sin el otro.
    /// </summary>
    public static async Task<string?> GetServicePaymentsAndVoucherBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default)
    {
        if (reservaId == 0) return null;

        var hasPayments = await db.Payments.AnyAsync(p => p.ReservaId == reservaId && !p.IsDeleted, ct);
        if (hasPayments)
            return "No se pueden eliminar servicios de una reserva con pagos realizados.";

        var hasIssuedVoucher = await db.Vouchers.AnyAsync(v => v.ReservaId == reservaId && v.Status == "Issued", ct);
        if (hasIssuedVoucher)
            return "No se pueden eliminar servicios de una reserva con vouchers ya emitidos. Anulá los vouchers primero.";

        return null;
    }

    /// <summary>
    /// Devuelve el motivo de bloqueo para borrar un pasajero o null si esta permitido.
    ///
    /// Reglas:
    ///  - Integridad de datos (2026-06-25): no se puede borrar el ULTIMO pasajero cuando la reserva
    ///    ya esta EN FIRME (Confirmada en adelante). Una reserva en firme con servicios resueltos y
    ///    cero pasajeros es un estado incoherente: el motor de estado no mira el roster, asi que sin
    ///    este guard la reserva quedaba Confirmada/Traveling con 0 pasajeros. Para deshacer la reserva
    ///    hay que Anular/Cancelar, NO borrar el ultimo pasajero. En pre-venta (Cotizacion/Presupuesto)
    ///    si se permite quedar en 0 (todavia se esta armando). Ver <see cref="EstadoReserva"/>.
    ///  - Pre-existente: la Reserva no puede estar en Operativo o Cerrado.
    ///  - Pre-existente: el pasajero no puede estar asignado a un voucher.
    ///  - Pre-existente: la Reserva no puede tener vouchers ya emitidos.
    ///  - C27: la Reserva no puede tener al menos una factura emitida (CAE no nulo).
    ///    Granularidad reserva-level — confirmado por Gaston + ARCA + Contable
    ///    (2026-05-06). Pax-level queda como ticket B1.7.
    /// </summary>
    public static async Task<string?> GetPassengerDeleteBlockReasonAsync(
        AppDbContext db,
        int passengerId,
        CancellationToken ct = default)
    {
        var passenger = await db.Passengers.AsNoTracking()
            .Where(p => p.Id == passengerId)
            .Select(p => new { p.ReservaId, ReservaStatus = p.Reserva != null ? p.Reserva.Status : null })
            .FirstOrDefaultAsync(ct);
        if (passenger == null) return null;

        if (passenger.ReservaStatus == EstadoReserva.Traveling || passenger.ReservaStatus == EstadoReserva.Closed)
            return "No se puede eliminar un pasajero de una reserva en estado Operativo o Cerrado.";

        var assignedToVoucher = await db.VoucherPassengerAssignments
            .AnyAsync(a => a.PassengerId == passengerId, ct);
        if (assignedToVoucher)
            return "No se puede eliminar el pasajero: esta asignado a uno o mas vouchers. Anulá los vouchers primero.";

        var reservaHasIssuedVoucher = await db.Vouchers
            .AnyAsync(v => v.ReservaId == passenger.ReservaId && v.Status == "Issued", ct);
        if (reservaHasIssuedVoucher)
            return "No se puede eliminar el pasajero: la reserva ya tiene vouchers emitidos.";

        var hasInvoiceWithCae = await db.Invoices
            .AnyAsync(i => i.ReservaId == passenger.ReservaId && !string.IsNullOrEmpty(i.CAE), ct);
        if (hasInvoiceWithCae)
        {
            return "No se puede eliminar al pasajero porque la reserva tiene una factura emitida (CAE). " +
                   "Anulá la factura con nota de credito primero.";
        }

        // Integridad de datos: el ULTIMO pasajero no se borra si la reserva esta en firme. Se evalua AL FINAL
        // (despues de los bloqueos fiscales/voucher, que tienen un mensaje mas especifico y accionable): este
        // guard cubre el caso puro de una reserva en firme con un solo pasajero, sin factura ni voucher. Solo
        // contamos OTROS pasajeros vivos (distintos a este); si no queda ninguno, este es el ultimo.
        if (RequiresAtLeastOnePassenger(passenger.ReservaStatus))
        {
            var hasOtherPassengers = await db.Passengers
                .AnyAsync(p => p.ReservaId == passenger.ReservaId && p.Id != passengerId, ct);
            if (!hasOtherPassengers)
            {
                return "No se puede borrar el ultimo pasajero de una reserva en firme. " +
                       "Si querés deshacer la reserva, usá Anular o Cancelar.";
            }
        }

        return null;
    }

    /// <summary>
    /// Integridad de datos (2026-06-25): true si una reserva en este estado debe conservar SIEMPRE al menos
    /// un pasajero (no se puede borrar el ultimo). Aplica a partir de Confirmada (la venta esta en firme y
    /// los servicios estan resueltos/comprometidos). En pre-venta (Cotizacion/Presupuesto/En gestion) el
    /// roster todavia se esta armando y puede quedar en 0, asi que NO entran aca.
    ///
    /// <para>DECISION A CONFIRMAR CON GASTON: hoy <see cref="EstadoReserva.InManagement"/> (En gestion) NO
    /// exige al menos un pasajero. Es la etapa donde el cliente ya acepto pero todavia se gestionan los
    /// servicios; se deja editar el roster con libertad (incluido quedar en 0 transitoriamente). Si el
    /// negocio prefiere que En gestion tambien exija >= 1 pasajero, agregar InManagement a esta lista.</para>
    /// </summary>
    private static bool RequiresAtLeastOnePassenger(string? reservaStatus)
    {
        if (string.IsNullOrWhiteSpace(reservaStatus)) return false;

        // Estados "en firme" donde la reserva ya no se esta armando: borrar el ultimo pasajero la dejaria
        // incoherente (Confirmada/En viaje/Cerrada/Esperando-reembolso con 0 pasajeros).
        return reservaStatus == EstadoReserva.Confirmed
            || reservaStatus == EstadoReserva.Traveling
            || reservaStatus == EstadoReserva.Closed
            || reservaStatus == EstadoReserva.PendingOperatorRefund;
    }

    /// <summary>
    /// Devuelve el motivo de bloqueo para borrar un pago o null si esta permitido.
    ///
    /// Reglas (C28 — cambio 2026-05-11 ratificado por arca-tax-expert + accounting-expert + Gaston):
    ///  - Bloquea si el pago tiene un Receipt en estado <c>Issued</c>. Hay que anular
    ///    el recibo primero (POST /api/payments/{id}/receipt/void).
    ///  - Receipt en estado <c>Voided</c> NO bloquea el delete del pago. La fila
    ///    Receipt se preserva (no se borra fisicamente) en la base de datos para
    ///    mantener numeracion correlativa para auditoria. Soft-delete del Payment
    ///    NO cascadea al Receipt (DeleteBehavior.Restrict configurado en AppDbContext
    ///    via WithOne(...).HasForeignKey&lt;PaymentReceipt&gt;(...)). Asi, los reportes
    ///    sobre PaymentReceipts (IgnoreQueryFilters() o con Include de Payment con
    ///    soft-delete filter on) siguen encontrando la fila Voided con su trazabilidad
    ///    completa (ReceiptNumber, VoidedAt, VoidedByUser*, VoidReason).
    ///  - Bloquea si el pago esta vinculado a una factura (RelatedInvoiceId).
    ///
    /// Regla previa (HASTA 2026-05-06): tambien bloqueaba con Voided. Cambio
    /// solicitado por Gaston al introducir el endpoint /receipt/void (Vendedores
    /// pueden anular recibos para corregir errores operativos comunes; obligar a
    /// contactar al Admin para borrar el Payment hace inviable el flujo).
    /// </summary>
    public static async Task<string?> GetPaymentDeleteBlockReasonAsync(
        AppDbContext db,
        int paymentId,
        CancellationToken ct = default)
    {
        var payment = await db.Payments.AsNoTracking()
            .Where(p => p.Id == paymentId)
            .Select(p => new { p.RelatedInvoiceId })
            .FirstOrDefaultAsync(ct);
        if (payment == null) return null;

        // Receipt es 0..1 por UNIQUE index en PaymentReceipts.PaymentId.
        // Materializamos a lista para no depender del orden de FirstOrDefault entre
        // InMemory y Postgres y para tener un patron defensivo si el index cambia.
        var receiptStatuses = await db.PaymentReceipts.AsNoTracking()
            .Where(r => r.PaymentId == paymentId)
            .Select(r => r.Status)
            .ToListAsync(ct);

        var hasIssued = receiptStatuses.Any(s => string.Equals(s, PaymentReceiptStatuses.Issued, StringComparison.OrdinalIgnoreCase));
        if (hasIssued)
        {
            // Si coexisten Issued + Voided (reemision), prevalece el mensaje del Issued:
            // ese es el recibo activo que el usuario tiene que anular primero.
            return "No se puede anular el pago porque tiene un comprobante vigente. Anula primero el comprobante.";
        }

        // Solo Voided: la fila se preserva via DeleteBehavior.Restrict pero el
        // delete (soft) del Payment puede proceder. La numeracion correlativa
        // queda intacta. Audit trail: VoidedAt/VoidedByUser*/VoidReason permanecen.

        if (payment.RelatedInvoiceId.HasValue)
        {
            return "No se puede eliminar el pago porque esta vinculado a una factura. " +
                   "Generá una nota de credito si corresponde.";
        }

        return null;
    }
}
