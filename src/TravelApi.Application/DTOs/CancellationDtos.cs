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
