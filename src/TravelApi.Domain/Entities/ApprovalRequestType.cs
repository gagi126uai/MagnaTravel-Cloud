namespace TravelApi.Domain.Entities;

/// <summary>
/// B1.15 Fase B' (2026-05-11): tipos de aprobacion soportados.
///
/// Cada valor mapea a una accion del sistema que requiere autorizacion adicional.
/// Cuando agregues un valor nuevo, sumar entrada a las defaults de la
/// configuracion de expiracion si aplica (settings <c>ApprovalExpirationDays:*</c>).
/// </summary>
public enum ApprovalRequestType
{
    /// <summary>Anular factura emitida (emite NC en AFIP). Consume Fase D.</summary>
    InvoiceAnnulment = 0,

    /// <summary>Cancelar reserva con cobros/facturas asociadas (decision 19).</summary>
    ReservationCancellationWithPayment = 1,

    /// <summary>Aplicar descuento sobre precio de referencia superior al umbral configurado.</summary>
    DiscountAboveThreshold = 2,

    /// <summary>Saltar el bloqueo "20 dias antes" para operativo / voucher (decision 22).</summary>
    PaymentDeadlineOverride = 3,

    /// <summary>Transferir reserva entre vendedores (cuando setting <c>EnableReservaTransferBetweenVendors</c> requiere aprobacion).</summary>
    ReservationTransfer = 4,

    /// <summary>Admin con motivo edita un campo congelado por CAE (MutationGuards). Uso excepcional.</summary>
    FrozenEntityMutation = 5,

    /// <summary>
    /// B1.15 (2026-05-11): anular comprobante interno de pago (PaymentReceipt).
    /// La fila se preserva (Status -> Voided) para mantener numeracion correlativa.
    /// </summary>
    ReceiptVoidance = 6,
}
