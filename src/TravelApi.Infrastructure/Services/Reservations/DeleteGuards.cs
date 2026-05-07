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

        if (!string.Equals(reserva.Status, EstadoReserva.Budget, StringComparison.OrdinalIgnoreCase))
        {
            return $"Solo se pueden eliminar reservas en estado Presupuesto. " +
                   $"Esta reserva esta en estado '{reserva.Status}'. Para reservas en otro estado, archivala (Cancelado).";
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

        return null;
    }

    /// <summary>
    /// Devuelve el motivo de bloqueo para borrar un servicio (hotel/transfer/package/
    /// flight/generico) o null si esta permitido.
    ///
    /// Reglas:
    ///  - C26: el estado de la Reserva padre debe ser Budget. En cualquier otro estado
    ///    (Confirmed/Traveling/Closed/Cancelled) hay que cancelar primero con el
    ///    proveedor — borrar silenciosamente perderia trazabilidad.
    ///  - Pre-existentes (consolidados de los <c>EnsureNoPaymentsAsync</c> duplicados
    ///    de ReservaService y BookingService): no debe haber pagos vivos del cliente
    ///    ni vouchers emitidos.
    /// </summary>
    public static async Task<string?> GetServiceDeleteBlockReasonAsync(
        AppDbContext db,
        int reservaId,
        CancellationToken ct = default,
        ILogger? logger = null)
    {
        if (reservaId == 0) return null; // servicios sin reserva (legacy) no aplican

        var reserva = await db.Reservas.AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => new { r.Status })
            .FirstOrDefaultAsync(ct);
        if (reserva == null) return null;

        if (!string.Equals(reserva.Status, EstadoReserva.Budget, StringComparison.OrdinalIgnoreCase))
        {
            return $"No se puede eliminar el servicio: la reserva esta en estado '{reserva.Status}'. " +
                   "Cancelá ese servicio con el proveedor primero (cambiá el status del servicio a 'Cancelado') " +
                   "y, si corresponde, cancelá la reserva.";
        }

        var paymentsReason = await GetServicePaymentsAndVoucherBlockReasonAsync(db, reservaId, ct);
        if (paymentsReason != null) return paymentsReason;

        // Cascade Servicio→Payments en AppDbContext (DeleteBehavior.Cascade): si quedo
        // algun Payment soft-deleted con ServicioReservaId apuntando a un servicio de
        // esta reserva, borrar el servicio lo hard-borraria por DB cascade saltandose
        // el query filter !IsDeleted. Riesgo fiscal/contable — preservamos auditoria.
        var hasSoftDeletedPaymentLinkedToService = await db.Payments
            .IgnoreQueryFilters()
            .AnyAsync(p => p.IsDeleted
                          && p.ServicioReservaId != null
                          && db.Servicios.Any(s => s.Id == p.ServicioReservaId && s.ReservaId == reservaId),
                      ct);
        if (hasSoftDeletedPaymentLinkedToService)
        {
            logger?.LogWarning(
                "Service delete blocked: soft-deleted payment(s) linked to service via ServicioReservaId. " +
                "ReservaId={ReservaId}. Cascade hard-delete riesgo fiscal — restaurar o reasignar antes de continuar.",
                reservaId);
            return "No se puede eliminar el servicio porque tiene pagos vinculados (incluso eliminados). " +
                   "Restaurá o reasigná los pagos primero.";
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

        return null;
    }

    /// <summary>
    /// Devuelve el motivo de bloqueo para borrar un pago o null si esta permitido.
    ///
    /// Reglas (C28):
    ///  - Bloquea si el pago tiene un Receipt asociado, sea cual sea su Status.
    ///    Voided tambien bloquea: el recibo ocupa numeracion correlativa y debe
    ///    preservarse para auditoria — confirmado por ARCA + Contable (2026-05-06).
    ///  - Bloquea si el pago esta vinculado a una factura (RelatedInvoiceId).
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

        // Caso real (emitido → anulado → reemitido): un payment puede tener 2 receipts.
        // FirstOrDefault sobre la lista no garantiza orden estable entre InMemory y Postgres,
        // asi que traemos los Status y decidimos en memoria.
        var receiptStatuses = await db.PaymentReceipts.AsNoTracking()
            .Where(r => r.PaymentId == paymentId)
            .Select(r => r.Status)
            .ToListAsync(ct);

        if (receiptStatuses.Count > 0)
        {
            var hasIssued = receiptStatuses.Any(s => string.Equals(s, PaymentReceiptStatuses.Issued, StringComparison.OrdinalIgnoreCase));
            if (hasIssued)
            {
                // Si coexisten Issued + Voided (reemision), prevalece el mensaje del Issued:
                // ese es el recibo activo que el usuario tiene que anular primero.
                return "No se puede eliminar el pago porque tiene un recibo emitido. Anulá el recibo primero.";
            }

            // Solo Voided: ocupan numeracion correlativa y deben preservarse para
            // auditoria — ARCA + Contable 2026-05-06.
            return "No se puede eliminar el pago porque tiene un recibo anulado que debe preservarse para auditoria. " +
                   "Contactá al administrador.";
        }

        if (payment.RelatedInvoiceId.HasValue)
        {
            return "No se puede eliminar el pago porque esta vinculado a una factura. " +
                   "Generá una nota de credito si corresponde.";
        }

        return null;
    }
}
