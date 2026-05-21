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

    /// <summary>
    /// FC1 (ADR-002, 2026-05-13): override generico de un invariante de negocio
    /// (patron <c>IBusinessInvariant</c> de ADR-001 review B4). Razon obligatoria
    /// >= 20 caracteres + audit reforzado. Usado por flujos donde el dominio
    /// permite forzar la operacion bajo responsabilidad explicita.
    /// </summary>
    InvariantOverride = 7,

    /// <summary>
    /// FC1 (ADR-002, 2026-05-13): solicitud al operador para que devuelva fondos
    /// fisicos sobre una cancelacion (T2 del flujo de cancelacion). Es un trigger
    /// administrativo, no implica salida de caja.
    /// </summary>
    ProviderRefundRequest = 8,

    /// <summary>
    /// FC1 (ADR-002, 2026-05-13): el cliente devuelve dinero ya retirado y la
    /// agencia lo re-acredita al operador. Diferenciado de
    /// <see cref="MisassociationReversal"/> porque aca SI hubo egreso fisico previo.
    /// Requiere audit reforzado (ver ADR-002 §2.10).
    /// </summary>
    ClientRefundReversal = 9,

    /// <summary>
    /// FC1 (ADR-002, 2026-05-13): correccion administrativa cuando un cashier
    /// asocio un <see cref="ApprovalRequestType"/> equivocado al BookingCancellation.
    /// Allocation original se marca <c>IsVoided</c> y se crea una nueva (ver ADR-002 §2.10).
    /// </summary>
    MisassociationReversal = 10,

    /// <summary>
    /// FC1.3 (ADR-009, 2026-05-21): solicitud de aprobacion para la liquidacion
    /// fiscal de una NC parcial Hotel.
    ///
    /// <para>El <c>EntityType</c> es siempre <c>"BookingCancellation"</c> y el
    /// <c>EntityId</c> apunta al BC. El detalle de la liquidacion calculada
    /// (montos, caso, regla aplicada) se serializa al campo
    /// <c>Metadata</c> JSON (ADR-009 §2.7) para audit + edicion admin.</para>
    ///
    /// <para>Se reutiliza la bandeja generica de <c>ApprovalRequestsController</c>;
    /// no hay UI nueva en Fase 1 (decision G2).</para>
    /// </summary>
    PartialCreditNoteApproval = 11,
}
