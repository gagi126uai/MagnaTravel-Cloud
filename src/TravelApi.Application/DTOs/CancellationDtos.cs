using System.ComponentModel.DataAnnotations;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

/// <summary>
/// FC1.2.1 v3 §3 (2026-05-17): DTOs del modulo de cancelacion/refund. Patron
/// del repo: requests con <c>record</c> + DataAnnotations, responses con
/// <c>class</c> + propiedades GET/SET (alineado a InvoiceDto, ApprovalRequestDto, etc.).
///
/// <para>
/// <b>Convencion PublicId-only (NB-02 plan v3)</b>: la API publica jamas expone
/// los ids legacy <c>int</c> de las entidades. El frontend trabaja siempre con
/// <c>Guid PublicId</c>. Esto permite renumerar/reorganizar la BD sin romper la
/// URL ni los clientes externos.
/// </para>
/// </summary>
public class BookingCancellationDto
{
    public Guid PublicId { get; set; }
    public string Status { get; set; } = string.Empty;

    public Guid ReservaPublicId { get; set; }
    public Guid CustomerPublicId { get; set; }
    public Guid SupplierPublicId { get; set; }

    public Guid OriginatingInvoicePublicId { get; set; }
    public Guid? CreditNoteInvoicePublicId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTime DraftedAt { get; set; }
    public DateTime? ConfirmedWithClientAt { get; set; }
    public DateTime? OperatorRefundDueBy { get; set; }
    public DateTime? ClosedAt { get; set; }

    public string DraftedByUserId { get; set; } = string.Empty;
    public string? DraftedByUserName { get; set; }
    public string? ConfirmedByUserId { get; set; }
    public string? ConfirmedByUserName { get; set; }

    public decimal AmountPaidAtCancellation { get; set; }
    public decimal EstimatedRefundAmount { get; set; }
    public decimal ReceivedRefundAmount { get; set; }

    /// <summary>
    /// Resumen visible del <c>FiscalSnapshot</c>. Solo se llena cuando el BC
    /// transiciono a <c>AwaitingFiscalConfirmation</c> en adelante (Drafted
    /// puede tener snapshot vacio). Strings legibles, no JSON estructurado —
    /// si el frontend necesita campos especificos, agregar al DTO.
    /// </summary>
    public FiscalSnapshotSummaryDto? FiscalSnapshot { get; set; }

    // FC1.2.1 (BR-V2-01): manual ARCA confirmation trazability.
    public DateTime? ArcaConfirmedManuallyAt { get; set; }
    public string? ArcaConfirmedManuallyByUserId { get; set; }
    public string? ArcaErrorMessage { get; set; }
}

/// <summary>
/// FC1.2.1: resumen del snapshot fiscal. NO contiene el VO completo (queremos
/// controlar exactamente que se expone hacia afuera).
/// </summary>
public class FiscalSnapshotSummaryDto
{
    public string? CurrencyAtEvent { get; set; }
    public decimal ExchangeRateAtOriginalInvoice { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime FetchedAt { get; set; }
    public string? CustomerTaxConditionAtEvent { get; set; }
    public string? SupplierTaxConditionAtEvent { get; set; }
    public string? AgencyTaxConditionAtEvent { get; set; }
    public string? ManualJustification { get; set; }
}

/// <summary>
/// FC1.2.1 §3.1: request para crear el BC en estado <c>Drafted</c> (T-1).
/// <c>Reason</c> es free-text del vendedor para que el contador entienda por
/// que se cancela. Min 10 chars (anti spam, pero corto: T-1 no es operacion
/// fiscal todavia, los 20 chars solo aplican al OverrideReason de Confirm).
/// </summary>
public record DraftCancellationRequest(
    [Required] Guid ReservaPublicId,

    [Required, MinLength(10), MaxLength(1000)]
    string Reason
);

/// <summary>
/// FC1.2.1 §3.2: request para transicionar el BC de <c>Drafted</c> a
/// <c>AwaitingFiscalConfirmation</c> (T0). Dispara la NC en AFIP via
/// <c>InvoiceService.EnqueueAnnulmentAsync</c>.
///
/// <para>
/// <b>SnapshotData</b> obligatorio: contiene las condiciones fiscales y TC que
/// quedan congelados en el <c>FiscalSnapshot</c> (INV-118).
/// </para>
///
/// <para>
/// <b>IsAdminOverride</b>: cuando hay alguna invariante que normalmente bloquearia
/// el T0 (ej. la reserva tiene multiples invoices y <c>OnePerReservaInvoicePolicy=true</c>),
/// un Admin puede forzar la operacion presentando un <c>InvariantOverride</c>
/// aprobado. En ese caso <c>OverrideReason</c> + <c>ApprovalRequestPublicId</c>
/// pasan a ser obligatorios (validacion en el service).
/// </para>
/// </summary>
public record ConfirmCancellationRequest(
    [Required] FiscalSnapshotData SnapshotData,
    bool IsAdminOverride,
    [MaxLength(500)] string? OverrideReason,
    Guid? ApprovalRequestPublicId
);

/// <summary>
/// FC1.2.1 §3.2.subset: payload del snapshot fiscal. Las 3 condiciones fiscales
/// se reciben raw (free-text desde el frontend, alineado al patron del repo).
/// El service las normaliza con <c>TaxConditionNormalizer</c> antes de persistir.
/// </summary>
public record FiscalSnapshotData(
    [Required, MaxLength(3)]
    string CurrencyAtEvent,

    [Range(0.000001, double.MaxValue)]
    decimal ExchangeRateAtOriginalInvoice,

    [Required]
    ExchangeRateSource Source,

    [MaxLength(500)]
    string? ManualJustification,

    [Required, MaxLength(50)]
    string AgencyTaxConditionAtEvent,

    [Required, MaxLength(50)]
    string SupplierTaxConditionAtEvent,

    [Required, MaxLength(50)]
    string CustomerTaxConditionAtEvent
);

/// <summary>
/// FC1.2.1 v3 §3.2.bis (BR-V2-01): payload del endpoint admin manual
/// <c>POST /api/cancellations/{publicId}/force-arca-confirmation</c>.
///
/// <para>
/// El admin elige a mano una NC ya emitida en AFIP que apunta al
/// <c>OriginatingInvoiceId</c> del BC. El service valida que la NC sea consistente
/// (mismo OriginalInvoiceId, tipo NC, Resultado=A) antes de empatar el estado.
/// </para>
/// </summary>
public record ForceArcaConfirmationRequest(
    [Required] Guid CreditNoteInvoicePublicId,
    [Required] Guid ApprovalRequestPublicId,

    [Required, MinLength(20), MaxLength(500)]
    string Reason
);

// =============================================================================
// FC1.2.2 (2026-05-18) — DTOs del modulo OperatorRefund (T2 del flujo).
// =============================================================================

/// <summary>
/// FC1.2.2 §3 (plan v3): payload para registrar el ingreso fisico que un
/// operador envia para cubrir cancelaciones (transferencia, cheque, etc.).
///
/// **Decisiones de diseno**:
///   - El monto es <c>ReceivedAmount</c> (lo que efectivamente llega a caja).
///     La distribucion contra BCs se hace por separado via <c>AllocateAsync</c>
///     porque un mismo ingreso puede cubrir N reservas distintas (N:M).
///   - <c>Currency</c> es ISO 4217 (3 chars). Validamos en el service que
///     coincida con la moneda del FiscalSnapshot del BC cuando se allocate.
///   - <c>ReceivedAt</c> es la fecha REAL del ingreso (cuando el dinero llego),
///     no <c>UtcNow</c>: si el cashier carga ayer una transferencia recibida
///     anteayer, queremos reflejar el dia correcto en el Libro de Caja.
/// </summary>
public record RecordOperatorRefundRequest(
    [Required] Guid SupplierPublicId,

    [Range(0.01, double.MaxValue, ErrorMessage = "El monto debe ser mayor a cero.")]
    decimal ReceivedAmount,

    [Required, MaxLength(3, ErrorMessage = "Currency es ISO 4217 (3 chars).")]
    string Currency,

    [Required] DateTime ReceivedAt,

    [MaxLength(50)] string? Method,

    [MaxLength(100)] string? Reference,

    [MaxLength(500)] string? Notes
);

/// <summary>
/// FC1.2.2 §3 (plan v3): linea de deduccion que viene en una <see cref="AllocateRefundRequest"/>.
///
/// <para>
/// **Por que esta tipificado**: <see cref="DeductionKind"/> tiene 10 valores con
/// reglas de validacion distintas (retenciones AR exigen certificado, ForeignTax
/// exige country, etc.). El service valida las reglas segun el kind antes de
/// persistir cada <see cref="DeductionLine"/>.
/// </para>
/// </summary>
public record DeductionLineRequest(
    [Required] DeductionKind Kind,

    [Range(0.01, double.MaxValue, ErrorMessage = "Amount debe ser mayor a cero.")]
    decimal Amount,

    [MaxLength(500)] string? Description,

    // Campos opcionales segun Kind. La validacion fina vive en el service.
    [MaxLength(50)] string? CertificateNumber,
    DateTime? CertificateDate,
    [MaxLength(500)] string? CertificatePdfUrl,
    [MaxLength(50)] string? Jurisdiction,
    [MaxLength(2)] string? ForeignCountryCode,
    [MaxLength(200)] string? SupportingDocumentRef,
    [MaxLength(1000)] string? JustificationComment,
    bool MissingFiscalSupport,
    [MaxLength(1000)] string? Comment,
    bool RequiresAccountingReview
);

/// <summary>
/// FC1.2.2 §3 (plan v3): payload para imputar parte de un ingreso del operador
/// contra UN BookingCancellation.
///
/// <para>
/// **Que es Gross vs Net**: <c>GrossAmount</c> es lo que el operador "dice" que
/// le toca a este BC (antes de descontar deducciones). <c>NetAmount</c> se
/// calcula automaticamente como <c>Gross - SUM(Deductions.Amount)</c> y es lo
/// que se acredita al cliente como <see cref="ClientCreditEntry"/>.
/// </para>
///
/// <para>
/// **Concurrencia N:M**: el CHECK SQL
/// <c>chk_OperatorRefundsReceived_allocated_not_exceeds</c> garantiza que
/// <c>SUM(allocations) &lt;= ReceivedAmount</c> incluso cuando dos cashiers
/// allocate paralelos contra el mismo refund.
/// </para>
/// </summary>
public record AllocateRefundRequest(
    [Required] Guid BookingCancellationPublicId,

    [Range(0.01, double.MaxValue, ErrorMessage = "GrossAmount debe ser mayor a cero.")]
    decimal GrossAmount,

    [Required] List<DeductionLineRequest> Deductions
);

/// <summary>
/// FC1.2.2 §3 (plan v3): payload del void de una allocation.
/// <c>Reason</c> obligatorio min 20 chars para auditoria fiscal.
/// </summary>
public record VoidAllocationRequest(
    [Required, MinLength(20), MaxLength(500)]
    string Reason
);

/// <summary>
/// FC1.2.2 §3 (plan v3): payload del reassociate (mueve allocation entre BCs).
/// Atomic: void de la vieja + create de la nueva en una sola tx.
/// </summary>
public record ReassociateAllocationRequest(
    [Required] Guid NewBookingCancellationPublicId,

    [Required, MinLength(20), MaxLength(500)]
    string Reason
);

/// <summary>
/// FC1.2.2 §3 (plan v3): respuesta del registro/lectura de un ingreso de operador.
/// Lo construye <see cref="OperatorRefundService"/> y lo retornan los endpoints
/// del controller (FC1.2.4). Contiene las allocations linkeadas para que la UI
/// pueda mostrar "Recibi $1000, asigne $300 a BC#A y $400 a BC#B, sobran $300".
/// </summary>
public class OperatorRefundReceivedDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public decimal ReceivedAmount { get; set; }
    public decimal AllocatedAmount { get; set; }
    public decimal RemainingCap { get; set; }
    public string Currency { get; set; } = "ARS";
    public DateTime ReceivedAt { get; set; }
    public string Method { get; set; } = "Transfer";
    public string? Reference { get; set; }
    public string ReceivedByUserId { get; set; } = string.Empty;
    public string ReceivedByUserName { get; set; } = string.Empty;
    public List<OperatorRefundAllocationDto> Allocations { get; set; } = new();
}

/// <summary>
/// FC1.2.2 §3 (plan v3): respuesta de una allocation N:M.
/// Incluye las deducciones tipificadas para que la UI pueda mostrar el desglose
/// "del bruto $500 le saque $50 de retencion IIBB Buenos Aires y $20 de banca".
/// </summary>
public class OperatorRefundAllocationDto
{
    public Guid PublicId { get; set; }
    public Guid RefundPublicId { get; set; }
    public Guid BookingCancellationPublicId { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal NetAmount { get; set; }
    public bool IsVoided { get; set; }
    public DateTime? VoidedAt { get; set; }
    public string? VoidedByUserId { get; set; }
    public string? VoidedReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public List<DeductionLineDto> Deductions { get; set; } = new();
}

/// <summary>FC1.2.2: respuesta de una linea de deduccion (todos los campos opcionales segun Kind).</summary>
public class DeductionLineDto
{
    public Guid PublicId { get; set; }
    public DeductionKind Kind { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public string? CertificateNumber { get; set; }
    public DateTime? CertificateDate { get; set; }
    public string? CertificatePdfUrl { get; set; }
    public string? Jurisdiction { get; set; }
    public string? ForeignCountryCode { get; set; }
    public string? SupportingDocumentRef { get; set; }
    public string? JustificationComment { get; set; }
    public bool MissingFiscalSupport { get; set; }
    public string? Comment { get; set; }
    public bool RequiresAccountingReview { get; set; }
}

// =============================================================================
// FC1.2.3 (2026-05-18) — DTOs del modulo ClientCredit (T3 del flujo).
// =============================================================================

/// <summary>
/// FC1.2.3 v3 §2.3 (2026-05-18): payload para que el cliente (o el cashier en
/// su nombre) retire saldo de un <see cref="ClientCreditEntry"/>.
///
/// <para>
/// <b>Kinds soportados</b> (ver <see cref="WithdrawalKind"/>):
/// <list type="bullet">
///   <item><c>KeptAsCredit</c>: el cliente no retira nada, deja el saldo vivo.
///         Genera un withdrawal "marca de decision" con Amount=0 (sin movimiento
///         de caja). Util para timeline del cliente: "el 12/05 decidio dejar
///         $X como credito".</item>
///   <item><c>PhysicalCash</c>: efectivo. Ley 25.345 valida que Amount no
///         supere <see cref="OperationalFinanceSettings.Ley25345ThresholdAmount"/>.
///         Si supera <see cref="OperationalFinanceSettings.PhysicalRefundAlertThreshold"/>
///         se logea alerta admin sin bloquear.</item>
///   <item><c>Transfer</c>: transferencia bancaria. Sin tope Ley 25.345.
///         <see cref="PaymentMethodOverride"/> permite trazar el detalle
///         operativo ("Transfer-BBVA", "MercadoPago", etc.).</item>
///   <item><c>AppliedToNewBooking</c>: el saldo se aplica como pago de otra
///         reserva del cliente. NO genera <see cref="ManualCashMovement"/>
///         (el <c>PaymentService</c> lo hara al registrar el pago en la nueva
///         reserva). <see cref="AppliedToReservaPublicId"/> apunta a la reserva
///         destino.</item>
///   <item><c>ReversedToOperator</c>: el cliente devuelve plata ya cobrada.
///         Requiere <see cref="ApprovalRequestPublicId"/> de tipo
///         <c>ClientRefundReversal</c> aprobado. Audit reforzado.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Validacion semantica</b>: el service decide que campos son obligatorios
/// segun el <c>Kind</c>. El payload los acepta todos como opcionales para que
/// el frontend no tenga que armar requests distintos por kind.
/// </para>
/// </summary>
public record WithdrawClientCreditRequest(
    [Required] WithdrawalKind Kind,

    // KeptAsCredit no consume saldo: Amount = 0 valido. El resto debe ser > 0.
    // No usamos [Range(0.01, ...)] porque excluiria KeptAsCredit; el service
    // valida el rango segun kind.
    [Range(0, double.MaxValue, ErrorMessage = "Amount no puede ser negativo.")]
    decimal Amount,

    // Solo se usa cuando Kind == Transfer (o PhysicalCash con descripcion).
    // Si null, el builder usa fallback por kind (Cash / Transfer).
    [MaxLength(50)] string? PaymentMethodOverride,

    // Solo se usa cuando Kind == AppliedToNewBooking. El service valida que la
    // reserva exista y pertenezca al mismo customer del entry (defense in depth).
    Guid? AppliedToReservaPublicId,

    // Solo se usa cuando Kind == ReversedToOperator. Debe ser un approval
    // ClientRefundReversal aprobado para entityType="ClientCreditEntry",
    // entityId=entry.Id, requestedBy=userId.
    Guid? ApprovalRequestPublicId,

    // Opcional: referencia externa para el ManualCashMovement (numero de
    // transferencia, comprobante bancario, etc.). El service lo persiste en
    // movement.Reference.
    [MaxLength(100)] string? Reference
);

/// <summary>
/// FC1.2.3: respuesta de un withdrawal ejecutado. Es el espejo de
/// <see cref="ClientCreditWithdrawal"/> + el PublicId del entry padre para
/// que el frontend pueda refetch el entry sin extra hops.
/// </summary>
public class ClientCreditWithdrawalDto
{
    public Guid PublicId { get; set; }
    public Guid EntryPublicId { get; set; }
    public WithdrawalKind Kind { get; set; }
    public decimal Amount { get; set; }
    public DateTime ExecutedAt { get; set; }
    public string ExecutedByUserId { get; set; } = string.Empty;
    public string ExecutedByUserName { get; set; } = string.Empty;

    // Reference al ManualCashMovement si aplico (PhysicalCash/Transfer/ReversedToOperator).
    // Null para KeptAsCredit y AppliedToNewBooking.
    public Guid? ManualCashMovementPublicId { get; set; }

    // Reference al approval consumido si aplico (ReversedToOperator).
    public string? ApprovalRequestId { get; set; }
}

/// <summary>
/// FC1.2.3: respuesta de query del saldo cliente con sus retiros.
/// La UI lo usa para mostrar "Saldo $X (de $Y inicial)" + timeline de retiros.
/// </summary>
public class ClientCreditEntryDto
{
    public Guid PublicId { get; set; }
    public Guid BookingCancellationPublicId { get; set; }
    public Guid CustomerPublicId { get; set; }
    public Guid OperatorRefundAllocationPublicId { get; set; }
    public decimal CreditedAmount { get; set; }
    public decimal RemainingBalance { get; set; }
    public bool IsFullyConsumed { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<ClientCreditWithdrawalDto> Withdrawals { get; set; } = new();
}
