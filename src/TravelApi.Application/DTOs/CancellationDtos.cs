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

    /// <summary>
    /// FC1.3 Fase 2 (RH-002): detalle de la liquidacion fiscal de la NC parcial
    /// (montos). Null cuando el BC no tiene liquidacion calculada (BCs FC1.2 puros,
    /// rechazados, o legacy sin backfill). Cuando existe, expone los componentes que
    /// el back-office necesita para auditar la NC parcial sin abrir el Metadata JSON
    /// del approval.
    /// </summary>
    public FiscalLiquidationSummaryDto? FiscalLiquidation { get; set; }

    // ===================================================================
    // ADR-013/014 (2026-06-02): estado de la penalidad y de la Nota de Debito.
    //
    // Se exponen como STRING (nombre del enum) por coherencia con Status, que
    // ya se serializa con .ToString(). El frontend los usa para mostrar en la
    // ficha de la reserva si la penalidad quedo Estimated (pendiente de que el
    // operador confirme el monto) o Confirmed, y en que estado quedo la ND
    // (NotApplicable / Pending / Issued / Failed / ManualReview).
    //
    // Con el flag EnableCancellationDebitNote OFF, estos campos quedan en sus
    // defaults conservadores (PenaltyStatus=Estimated, DebitNoteStatus=NotApplicable)
    // exactamente como hoy: agregarlos al DTO NO cambia el comportamiento del backend.
    // ===================================================================

    /// <summary>
    /// ADR-013 (R5): estado de la penalidad. "Estimated" = el operador todavia no
    /// confirmo el monto (NO hay ND); "Confirmed" = monto confirmado (habilita la ND).
    /// </summary>
    public string PenaltyStatus { get; set; } = string.Empty;

    /// <summary>
    /// ADR-013 §3.10: estado observable de la ND. "NotApplicable" (no corresponde ND),
    /// "Pending" (encolada, esperando CAE), "Issued" (con CAE), "Failed" (rebote ARCA),
    /// "ManualReview" (el gating ruteo a revision manual).
    /// </summary>
    public string DebitNoteStatus { get; set; } = string.Empty;

    /// <summary>
    /// ADR-014 (read-model, 2026-06-23): true si AHORA MISMO se puede disparar la
    /// confirmacion diferida de la multa del operador (que emite la Nota de Debito) sobre
    /// esta cancelacion. Refleja las precondiciones de ESTADO del BC que valida
    /// <c>ConfirmPenaltyAsync</c> — NO el permiso del usuario ni el 4-eyes (esos los resuelve
    /// el endpoint confirm-penalty al ejecutar). El frontend lo usa para mostrar el boton
    /// "Confirmar multa del operador" habilitado o, si es false, un aviso con el motivo
    /// (<see cref="ConfirmPenaltyBlockedReason"/>): "esperá a que la NC tenga CAE",
    /// "ya tiene Nota de Débito", "la facturación de Notas de Débito está deshabilitada".
    ///
    /// <para><b>Por que es seguro</b>: es una lectura derivada de campos que el DTO ya expone
    /// (Status, CreditNoteInvoicePublicId, PenaltyStatus, DebitNoteStatus). NO afecta el
    /// comportamiento del backend: confirm-penalty revalida TODAS las precondiciones
    /// server-side. Este bool es solo una pista de UI para no ofrecer un boton que va a rebotar.</para>
    ///
    /// <para><b>Nota</b>: refleja solo precondiciones de ESTADO. Un caller que mande EXPLICITAMENTE
    /// un <c>ConceptKind</c> que no emite ND (ej. un seguro) puede rebotar igual con <c>true</c>
    /// aca (precondicion 5 de confirm-penalty). El panel de multa del operador no manda concepto
    /// explicito, asi que para su caso este bool es exacto.</para>
    /// </summary>
    public bool CanConfirmPenalty { get; set; }

    /// <summary>
    /// ADR-014 (read-model, 2026-06-23): cuando <see cref="CanConfirmPenalty"/> es false,
    /// codigo legible del motivo para que el frontend muestre el aviso correcto. Null cuando
    /// <see cref="CanConfirmPenalty"/> es true. Valores posibles:
    /// <list type="bullet">
    ///   <item><c>DebitNoteFeatureDisabled</c>: el flag <c>EnableCancellationDebitNote</c> esta OFF.</item>
    ///   <item><c>CreditNoteNotYetIssued</c>: la NC total todavia no tiene CAE (estado no post-NC
    ///         o sin <c>CreditNoteInvoicePublicId</c>).</item>
    ///   <item><c>DebitNoteAlreadyInPlay</c>: la penalidad ya fue confirmada o la ND ya esta
    ///         emitida/encolada (no se vuelve a emitir).</item>
    /// </list>
    /// </summary>
    public string? ConfirmPenaltyBlockedReason { get; set; }
}

/// <summary>
/// FC1.3 Fase 2 (RH-002): resumen del <c>FiscalLiquidation</c> owned VO para exponer
/// en la API. Mismo patron que <see cref="FiscalSnapshotSummaryDto"/>: clase con
/// GET/SET, solo los campos que el frontend necesita ver.
/// </summary>
public class FiscalLiquidationSummaryDto
{
    public decimal OriginalInvoiceAmount { get; set; }
    public decimal CancellationAmount { get; set; }
    public decimal OperatorPenaltyAmount { get; set; }
    public decimal NonRefundableItemsAmount { get; set; }
    public decimal FiscalAmountToCredit { get; set; }
    public decimal AmountToRefundCustomer { get; set; }
    public decimal FinalNetInvoiced { get; set; }
    public string? Currency { get; set; }
    public DateTime? ComputedAt { get; set; }
    public string? ComputedByUserId { get; set; }
    public string? ComputedByUserName { get; set; }
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
/// ADR-025 (DT.3.1, 2026-06-13): request para cancelar UN servicio dentro de una reserva, dejando el
/// resto del file vivo (cancelacion PARCIAL). NO mueve el estado de la reserva (decision sellada #1 de
/// Gaston): solo marca el servicio cancelado; el saldo del cliente baja solo (el servicio cancelado sale
/// del calculo por ServiceResolutionRules) y la deuda del operador de ESE servicio baja en la misma
/// operacion (B1).
///
/// <para><b>Que tabla/servicio</b>: <see cref="ServiceTable"/> + <see cref="ServicePublicId"/> identifican
/// el servicio puntual. El service VALIDA server-side que el servicio pertenece a la reserva (no se confia
/// en el frontend, espejo de INV-151).</para>
///
/// <para><b>Fiscal</b>: NO se emite NC automatica (decision #3). El borrador/calculo queda para revision
/// manual. La penalidad se clasifica por linea (pass-through vs cargo propio).</para>
/// </summary>
public record CancelServiceRequest(
    [Required] Guid ReservaPublicId,

    /// <summary>En que tabla vive el servicio. Valores: Generic|Flight|Hotel|Transfer|Package|Assistance.</summary>
    [Required] string ServiceTable,

    [Required] Guid ServicePublicId,

    [Required, MinLength(10), MaxLength(1000)]
    string Reason
);

/// <summary>
/// ADR-025 (DT.3.1): resultado de cancelar un servicio. Devuelve datos minimos para que la UI muestre el
/// contador "N de M servicios cancelado" sin re-consultar (decision #1: no hay estado nuevo de reserva).
/// </summary>
public record CancelServiceResultDto(
    Guid ReservaPublicId,
    Guid ServicePublicId,
    string ServiceTable,
    int CancelledServicesCount,
    int TotalServicesWithSupplierCount
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
    Guid? ApprovalRequestPublicId,

    // ===================================================================
    // ADR-013 (2026-06-01): captura de la clasificacion de la penalidad.
    //
    // TODOS opcionales (nullable) para NO romper el contrato actual: si el
    // frontend no manda nada, el BC queda con sus defaults conservadores
    // (pass-through / Estimated) y el comportamiento es byte-identico a hoy
    // (NC total, sin ND). Con el flag EnableCancellationDebitNote OFF estos
    // campos se ignoran aunque vengan seteados.
    //
    // El usuario, al confirmar la penalidad con el operador, informa:
    //  - ConceptKind: si la penalidad es propia de la agencia (ingreso gravado
    //    -> ND) o del operador (pass-through -> NO ND). Si null, el service
    //    sugiere el default a partir de Supplier.PenaltyOwnership del operador.
    //  - PenaltyStatus: Confirmed cuando el operador confirmo el monto.
    //  - DebitNotePurpose: la finalidad (MVP solo PenaltyOrCancellationCharge).
    //  - ConfirmedPenaltyAmount: el monto confirmado de la penalidad.
    // ===================================================================

    /// <summary>
    /// ADR-013 (R4): naturaleza fiscal del concepto. Null = el service decide el
    /// default segun el operador (Supplier.PenaltyOwnership). Clasificar como
    /// ingreso propio de la agencia (AgencyManagementFee / AgencyCancellationFee)
    /// exige el permiso <c>cancellations.classify_agency_penalty</c>.
    /// </summary>
    CancellationConceptKind? PenaltyConceptKind = null,

    /// <summary>ADR-013 (R5): estado de la penalidad. Solo Confirmed dispara la ND.</summary>
    PenaltyStatus? PenaltyStatus = null,

    /// <summary>ADR-013 (R3): finalidad de la ND. MVP automatiza PenaltyOrCancellationCharge.</summary>
    DebitNotePurpose? DebitNotePurpose = null,

    /// <summary>
    /// ADR-013 (§3.8): monto confirmado de la penalidad. Debe ser &gt; 0 si se
    /// informa. Se congela como <c>PenaltyAmountAtEvent</c> al disparar la ND.
    /// </summary>
    [Range(0.01, double.MaxValue, ErrorMessage = "El monto de la penalidad debe ser mayor a cero.")]
    decimal? ConfirmedPenaltyAmount = null
);

/// <summary>
/// ADR-014 (§3.1, 2026-06-02): payload del endpoint de confirmacion DIFERIDA de la
/// penalidad — <c>PATCH /api/cancellations/{publicId}/confirm-penalty</c>.
///
/// <para>Se usa DIAS DESPUES de la cancelacion, cuando el operador confirma el monto
/// definitivo de la penalidad PROPIA de la agencia. Dispara la emision de la ND
/// reusando el motor existente. Es la decision fiscalmente mas sensible del flujo: el
/// permiso <c>cancellations.classify_agency_penalty</c> se resuelve server-side y, segun
/// el monto / el soporte documental, puede exigir 4-eyes (§3.6).</para>
///
/// <para><b>Diferencias con <see cref="ConfirmCancellationRequest"/></b>: alla los campos
/// de penalidad son opcionales (clasificacion al confirmar la cancelacion en el Dia 0);
/// aca <c>ConfirmedPenaltyAmount</c> y <c>OperatorConfirmationDate</c> son OBLIGATORIOS
/// (el flujo diferido existe justamente para confirmar el monto y su fecha).</para>
/// </summary>
public record ConfirmPenaltyRequest(
    /// <summary>
    /// ADR-014 (R4): naturaleza fiscal del concepto. El frontend ofrece 2 opciones:
    /// <c>AgencyManagementFee</c> (cargo de gestion) o <c>AgencyCancellationFee</c>
    /// (cargo de cancelacion). Null = el service usa el default por operador
    /// (Supplier.PenaltyOwnership). Clasificar como ingreso propio exige el permiso
    /// <c>cancellations.classify_agency_penalty</c>.
    /// </summary>
    CancellationConceptKind? ConceptKind,

    /// <summary>ADR-014 (§3.1): el monto definitivo de la penalidad que confirmo el operador. Obligatorio &gt; 0.</summary>
    [Range(0.01, double.MaxValue, ErrorMessage = "El monto confirmado de la penalidad debe ser mayor a cero.")]
    decimal ConfirmedPenaltyAmount,

    /// <summary>
    /// ADR-014 (§3.1, §3.3): la fecha REAL en que el operador confirmo el monto. Eje
    /// fiscal del plazo (RG 4540) y del devengamiento. El service valida que no sea
    /// futura ni anterior a la fecha de la cancelacion. Obligatoria.
    /// </summary>
    [Required]
    DateTime OperatorConfirmationDate,

    /// <summary>ADR-014 (R3): finalidad de la ND. Null = default PenaltyOrCancellationCharge (el unico que el MVP automatiza).</summary>
    DebitNotePurpose? DebitNotePurpose = null,

    /// <summary>
    /// CAMBIO 3 (2026-06-24): moneda de la multa que retuvo el operador (ISO 4217, 3 chars). Al operador se le
    /// paga en USD, asi que la multa puede ser USD o ARS. Es SOLO captura/registro: persistimos esta moneda en
    /// la(s) <c>BookingCancellationLine</c> del BC para tener la verdad de lo que retuvo el operador.
    ///
    /// <para><b>Opcional</b>: si el request no la trae, el service usa por defecto la moneda de la linea/servicio
    /// cancelado. <b>NO cambia la moneda en la que se EMITE la Nota de Debito al cliente</b> (eso sigue como hoy,
    /// territorio del contador). Wire de esta moneda a la emision/FX de la ND es follow-up que requiere firma del
    /// contador.</para>
    /// </summary>
    [MaxLength(3, ErrorMessage = "La moneda debe ser un código de 3 letras (por ejemplo: ARS o USD).")]
    string? PenaltyCurrency = null,

    /// <summary>
    /// ADR-014 (§3.1, §3.6): referencia/URL del soporte documental del acuerdo del
    /// operador (mail / PDF). Opcional. Si NO se adjunta, el service exige 4-eyes
    /// (confirmar una penalidad propia sin respaldo es el caso de mayor riesgo).
    /// </summary>
    [MaxLength(500)] string? SupportingDocumentReference = null,

    // 4-eyes: mismo patron que ConfirmCancellationRequest. Si el caso exige doble firma
    // (sin soporte documental O monto sobre el umbral) y el caller no trae un approval
    // valido, el service tira ApprovalRequiredException -> 409 requiresApproval. El caller
    // crea el approval y reintenta pasando el ApprovalRequestPublicId.
    [MaxLength(500)] string? OverrideReason = null,
    Guid? ApprovalRequestPublicId = null
);

/// <summary>
/// ADR-014 (M1, §3.2, 2026-06-02): forma de entrada COMUN de la clasificacion de la
/// penalidad. Tanto el path SINCRONO (<see cref="ConfirmCancellationRequest"/>, Dia 0)
/// como el DIFERIDO (<see cref="ConfirmPenaltyRequest"/>, Dia N) construyen este record
/// y se lo pasan a <c>CaptureDebitNoteClassification</c>.
///
/// <para><b>Por que un record comun y no una firma polimorfica</b> (M1): cambiar la firma
/// de <c>CaptureDebitNoteClassification</c> a aceptar dos tipos distintos (o un tipo base)
/// arriesgaria el path sincrono ya probado. Extraer un record neutro mantiene la logica del
/// metodo intacta: solo cambia la FORMA de pasar los datos, no QUE hace con ellos. Cada
/// request mapea sus campos a este record con una funcion explicita.</para>
/// </summary>
public record PenaltyClassificationInput(
    CancellationConceptKind? PenaltyConceptKind,
    PenaltyStatus? PenaltyStatus,
    DebitNotePurpose? DebitNotePurpose,
    decimal? ConfirmedPenaltyAmount
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

    [Required, MaxLength(3, ErrorMessage = "La moneda debe ser un código de 3 letras (por ejemplo: ARS o USD).")]
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

    [Range(0.01, double.MaxValue, ErrorMessage = "El monto debe ser mayor a cero.")]
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

    [Range(0.01, double.MaxValue, ErrorMessage = "El monto debe ser mayor a cero.")]
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
    [Range(0, double.MaxValue, ErrorMessage = "El monto no puede ser negativo.")]
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
