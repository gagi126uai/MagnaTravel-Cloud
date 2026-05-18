using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.3, 2026-05-13): aggregate root del flujo de cancelacion
/// de una reserva. Modela los 4 momentos del flujo (T0..T3) descritos en
/// ADR-002 §2.4 y captura la "foto fiscal" del cliente/operador/agencia.
///
/// Aggregate boundary:
///  - 1:1 con <see cref="Reserva"/> (unique constraint en BD, INV-081).
///  - Owns a <see cref="FiscalSnapshot"/> (value object inmutable).
///  - NO posee los <c>OperatorRefundAllocation</c> ni los <c>ClientCreditEntry</c>:
///    esos viven en otros aggregates (<see cref="OperatorRefundReceived"/> y
///    <see cref="ClientCreditEntry"/>) por las razones descritas en ADR-002 §4.
///
/// Concurrency:
///  - <c>UseXminAsConcurrencyToken()</c> via shadow property "xmin". Cualquier
///    update entre dos sesiones que toquen el mismo BC lanza
///    <c>DbUpdateConcurrencyException</c> -> el caller reintenta o reporta 409.
/// </summary>
public class BookingCancellation : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // ===== Relaciones obligatorias =====

    /// <summary>FK a la reserva que se cancela. UNIQUE en BD (INV-081, una cancelacion activa por reserva).</summary>
    public int ReservaId { get; set; }
    public Reserva Reserva { get; set; } = null!;

    /// <summary>FK al cliente. Snapshot fiscal preserva la condicion fiscal del cliente al momento de T0.</summary>
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>FK al operador/proveedor. Snapshot fiscal preserva la condicion fiscal del operador al momento de T0.</summary>
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>
    /// FK a la factura original (A/B/C) que sera anulada. UNIQUE como precondicion
    /// (INV-100, <c>OnePerReservaInvoicePolicy=true</c>): si la reserva tiene mas
    /// de una factura activa, la cancelacion rechaza con mensaje claro.
    /// </summary>
    public int OriginatingInvoiceId { get; set; }
    public Invoice OriginatingInvoice { get; set; } = null!;

    /// <summary>FK a la NC fiscal emitida en T0. Null hasta que el service llame a InvoiceService.AnnulAsync.</summary>
    public int? CreditNoteInvoiceId { get; set; }
    public Invoice? CreditNoteInvoice { get; set; }

    // ===== Estado de la maquina =====

    public BookingCancellationStatus Status { get; set; } = BookingCancellationStatus.Drafted;

    /// <summary>Motivo declarado por el vendedor (auditoria fiscal). Obligatorio.</summary>
    [Required]
    [MaxLength(1000)]
    public string Reason { get; set; } = string.Empty;

    // ===== Timestamps por momento =====

    public DateTime DraftedAt { get; set; } = DateTime.UtcNow;

    /// <summary>T0: confirmacion con el cliente + emision de NC. Trigger de la maquina de estados.</summary>
    public DateTime? ConfirmedWithClientAt { get; set; }

    /// <summary>Momento en que se solicito formalmente el refund al operador (T1+).</summary>
    public DateTime? OperatorRequestedAt { get; set; }

    /// <summary>
    /// Deadline configurado (T0 + <c>OperatorRefundTimeoutDays</c>). Si se vence
    /// sin allocations, el job nocturno mueve el BC a <c>AbandonedByOperator</c>.
    /// </summary>
    public DateTime? OperatorRefundDueBy { get; set; }

    /// <summary>Cierre administrativo del BC (Closed o AbandonedByOperator o Aborted).</summary>
    public DateTime? ClosedAt { get; set; }

    // ===== Trazabilidad de usuarios =====

    [Required]
    [MaxLength(450)]
    public string DraftedByUserId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DraftedByUserName { get; set; }

    /// <summary>Quien dispara T0 (confirmar con cliente). Puede coincidir con DraftedBy o no.</summary>
    [MaxLength(450)]
    public string? ConfirmedByUserId { get; set; }

    [MaxLength(200)]
    public string? ConfirmedByUserName { get; set; }

    // ===== Snapshot economico =====

    /// <summary>Total cobrado al cliente al momento de T0 (suma de <c>Payment.Amount</c> activos sobre la reserva).</summary>
    public decimal AmountPaidAtCancellation { get; set; }

    /// <summary>Refund estimado pre-deducciones (informativo, NO bloquea el ingreso real). Sirve para alertas en la UI.</summary>
    public decimal EstimatedRefundAmount { get; set; }

    /// <summary>
    /// Denormalizado: <c>SUM(allocations.NetAmount)</c> recibido sobre este BC.
    /// Se mantiene actualizado por <c>OperatorRefundService.RecordRefundAsync</c>
    /// para evitar agregaciones en cada lectura. Hay un test invariante periodico
    /// que reconcilia este valor contra la suma real (mitigacion documentada en ADR-002 §12.6).
    /// </summary>
    public decimal ReceivedRefundAmount { get; set; }

    // ===== Foto fiscal (value object owned) =====

    /// <summary>
    /// Owned value object con la foto fiscal del momento T0.
    ///
    /// Inicializacion (review BR2, 2026-05-14):
    ///  - El service que crea el BC en <c>Drafted</c> DEBE asignar este objeto (no se
    ///    auto-inicializa con <c>= new()</c> a proposito: un default vacio escondido
    ///    seria una bomba si llegaba a persistirse).
    ///  - Para transicionar a <c>AwaitingFiscalConfirmation</c> (T0), el service DEBE
    ///    completar al menos: <c>Source != Unset</c>, <c>ExchangeRateAtOriginalInvoice &gt; 0</c>,
    ///    <c>CurrencyAtEvent != null</c>, <c>FetchedAt</c>. El CHECK SQL
    ///    <c>chk_BookingCancellations_fiscalsnapshot_consistent</c> bloquea cualquier
    ///    transicion incompleta (INV-118 / ADR-002 §2.7).
    ///  - Es <c>null!</c> en lugar de <c>required</c> porque EF Core 8 con
    ///    <c>OwnsOne</c> no soporta required navigation propertis de owned types
    ///    sin friccion en el modelado (verificable en stack 8.0.x).
    /// </summary>
    public FiscalSnapshot FiscalSnapshot { get; set; } = null!;

    // ===== Backfill / legacy =====

    /// <summary>
    /// Marca para datos previos al deploy de FC1 (regla de backfill, ADR-002 §5.2):
    ///   - null  = registro legacy (NCs historicas, modelo viejo).
    ///   - false = creado con el modelo nuevo (FC1).
    /// Es tri-state porque permite distinguir "no aplica" vs "aplica explicitamente".
    /// </summary>
    public bool? IsLegacyPreCancellationModel { get; set; }

    // ============================================================
    // FC1.2.1 v3 §10.1 (BR-V2-01, 2026-05-17): trazabilidad del escape hatch
    // manual cuando AFIP confirmo la NC pero el callback automatico fallo
    // (ej. job zombie en Hangfire, excepcion no recuperable).
    // ============================================================

    /// <summary>
    /// FC1.2.1 (BR-V2-01): UTC en que un Admin forzo la transicion fiscal
    /// (<c>ForceArcaConfirmationAsync</c>). <b>Null</b> si la transicion paso por el
    /// flujo automatico (callback Hangfire). Si tiene valor, en auditoria se puede
    /// distinguir manual vs automatico.
    /// </summary>
    public DateTime? ArcaConfirmedManuallyAt { get; set; }

    /// <summary>
    /// FC1.2.1 (BR-V2-01): UserId del Admin que ejecuto <c>ForceArcaConfirmationAsync</c>.
    /// Null si la transicion fue automatica. La FK se deja "logica" (string del Id) sin
    /// referencial constraint a <c>AspNetUsers</c> a proposito: si el user se elimina,
    /// el rastro de quien forzo la operacion debe sobrevivir (auditoria fiscal).
    /// </summary>
    [MaxLength(450)]
    public string? ArcaConfirmedManuallyByUserId { get; set; }

    /// <summary>
    /// FC1.2.1: mensaje de error que AFIP devolvio cuando rechazo la NC.
    /// Persistido aca para que el back-office vea el motivo sin tener que abrir
    /// los logs de Hangfire o consultar el Invoice. Util cuando el BC queda en
    /// <see cref="BookingCancellationStatus.ArcaRejected"/>.
    /// </summary>
    [MaxLength(1000)]
    public string? ArcaErrorMessage { get; set; }
}
