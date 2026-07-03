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

    /// <summary>
    /// FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.1, cierra RH-002): owned value
    /// object con el detalle COMPLETO de la liquidacion fiscal de la NC parcial
    /// (los montos, no solo el resumen).
    ///
    /// <para><b>Nullable a proposito</b>: los BCs creados en Fase 1 (pre-F2.1) tienen
    /// el detalle solo en <c>ApprovalRequest.Metadata</c> JSON, no en columnas
    /// dedicadas. El backfill de la migracion <c>Fase2_M1</c> popula este VO para los
    /// BCs Fase 1 que tienen un approval asociado; los rechazados o que nunca pasaron
    /// por manual review quedan en null (la liquidacion no aplica).</para>
    ///
    /// <para><b>Doble-write invariante (RH-002)</b>: cuando el service escribe la
    /// liquidacion (ConfirmAsync path NC parcial + EditLiquidationAsync), DEBE poblar
    /// este VO Y el JSON del Metadata con los MISMOS valores. Ver
    /// <see cref="FiscalLiquidation"/> para el detalle del invariante y la coherencia
    /// de <c>ComputedAt</c> con <see cref="LiquidationComputedAt"/>.</para>
    /// </summary>
    public FiscalLiquidation? FiscalLiquidation { get; set; }

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

    // ============================================================
    // FC1.3 (ADR-009 §2.3.2, 2026-05-21): persistencia MINIMA del
    // resultado del clasificador fiscal NC parcial. Decision GR-004:
    // NO persistimos la liquidacion entera (montos, items, formula);
    // solo el resumen (kind + motivos + timestamps + FK al approval).
    // El detalle completo del calculo vive en
    // <c>ApprovalRequest.Metadata</c> JSON, y SOLO cuando hubo manual
    // review. Si auto-aprobo, el calculator es deterministico y se
    // re-ejecuta en Fase 2 cuando sea necesario.
    // ============================================================

    /// <summary>
    /// FC1.3 (ADR-009): tipo de NC clasificado por <c>IFiscalLiquidationCalculator</c>.
    /// Fase 1 solo persiste <see cref="CreditNoteKind.PartialOnOriginal"/>;
    /// <see cref="CreditNoteKind.TotalPlusNewInvoice"/> dispara rechazo en Confirm
    /// (GR-001) y por lo tanto NUNCA queda persistido en BD.
    /// Null hasta que el calculator corra (BC con flag FC1.3 OFF o caso trivial FC1.2).
    /// </summary>
    public CreditNoteKind? CreditNoteKind { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): bitflag de motivos que activaron revision manual.
    /// Default <see cref="Entities.ReviewRequiredReason.None"/> (=0) significa
    /// "el calculator no encontro motivos, puede auto-aprobarse".
    ///
    /// <para>Se persiste para queries de auditoria/reporting tipo:
    /// "cuantas cancelaciones del mes dispararon manual review por items no
    /// reintegrables". Una sola query bitwise sin tocar JSON.</para>
    /// </summary>
    public ReviewRequiredReason ReviewRequiredReason { get; set; } = ReviewRequiredReason.None;

    /// <summary>
    /// FC1.3 (ADR-009): momento UTC en que corrio el calculator. Null si nunca corrio
    /// (BC en flujo FC1.2 puro). El CHECK SQL <c>chk_BookingCancellations_creditnotekind_consistent</c>
    /// garantiza que si este valor no es null, <see cref="CreditNoteKind"/> tampoco lo es.
    /// </summary>
    public DateTime? LiquidationComputedAt { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): userId del usuario que disparo el Confirm (y por lo tanto la
    /// corrida del calculator). Se guarda como string libre sin FK formal a
    /// <c>AspNetUsers</c> — mismo patron que <see cref="DraftedByUserId"/>: si la
    /// cuenta se elimina, el rastro fiscal sobrevive.
    /// </summary>
    [MaxLength(450)]
    public string? LiquidationComputedByUserId { get; set; }

    [MaxLength(200)]
    public string? LiquidationComputedByUserName { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): FK al <see cref="ApprovalRequest"/> tipo
    /// <c>PartialCreditNoteApproval=11</c> que aprueba/rechaza la liquidacion.
    /// Null hasta que el BC entra a <see cref="BookingCancellationStatus.ManualReviewPending"/>.
    ///
    /// <para>El CHECK SQL <c>chk_BookingCancellations_manualreview_approvalref</c>
    /// garantiza que cualquier Status en {9, 10, 11} tiene este FK seteado.</para>
    ///
    /// <para><b>OnDelete: Restrict</b> — preservar el rastro del approval aunque
    /// alguien intente eliminarlo. Mismo patron que <c>Invoice.AnnulmentApprovalRequestId</c>.</para>
    /// </summary>
    public int? PartialCreditNoteApprovalRequestId { get; set; }
    public ApprovalRequest? PartialCreditNoteApprovalRequest { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): trazabilidad del usuario admin que tomo la decision en
    /// la revision manual (aprobar/rechazar). Null si el BC paso por flujo auto.
    /// Mismo patron de string libre que arriba.
    /// </summary>
    [MaxLength(450)]
    public string? ManualReviewerUserId { get; set; }

    [MaxLength(200)]
    public string? ManualReviewerUserName { get; set; }

    /// <summary>FC1.3 (ADR-009): momento UTC en que el admin resolvio la revision manual.</summary>
    public DateTime? ManualReviewedAt { get; set; }

    /// <summary>
    /// FC1.3 (ADR-009): comentario obligatorio del admin al resolver la revision
    /// manual. El ADR especifica longitud minima distinta segun el threshold del
    /// monto (validacion en service): &gt;= 20 chars para AdminReview, &gt;= 100
    /// chars para AccountingReview y para GR-005 (single admin self-approval).
    /// </summary>
    [MaxLength(1000)]
    public string? ManualReviewComment { get; set; }

    // ============================================================
    // ADR-013 (2026-06-01): Nota de Debito por penalidad propia de la agencia.
    //
    // Todo este bloque es ADITIVO y nullable / con default conservador. Con el
    // flag EnableCancellationDebitNote OFF, NADIE setea estos campos y el
    // comportamiento es byte-identico a hoy (NC total, sin ND).
    //
    // Los campos se dividen en tres grupos:
    //  (1) clasificacion del evento  -> que tipo de penalidad es y en que estado.
    //  (2) vinculo + estado de la ND -> idempotencia + observabilidad (bandeja).
    //  (3) snapshot fiscal congelado -> con que reglas se emitio (auditoria).
    // ============================================================

    // ---- (1) Clasificacion del evento ----

    /// <summary>
    /// ADR-013 (R5): estado de la penalidad. Solo <see cref="Entities.PenaltyStatus.Confirmed"/>
    /// habilita emitir la ND (no se emite comprobante sobre un estimado). Default
    /// <see cref="Entities.PenaltyStatus.Estimated"/> (conservador = NO ND).
    /// </summary>
    public PenaltyStatus PenaltyStatus { get; set; } = PenaltyStatus.Estimated;

    /// <summary>
    /// ADR-013 (R4): naturaleza fiscal del concepto (define si hay ND propia o no).
    /// Default <see cref="Entities.CancellationConceptKind.OperatorPenaltyPassThrough"/>
    /// (conservador = NO ND, igual a hoy). Su cambio se audita (es la decision
    /// fiscalmente mas sensible, §3.11).
    /// </summary>
    public CancellationConceptKind ConceptKind { get; set; } = CancellationConceptKind.OperatorPenaltyPassThrough;

    /// <summary>
    /// ADR-013 (R3): finalidad de la ND. Null mientras no haya ND. El MVP solo
    /// automatiza <see cref="Entities.DebitNotePurpose.PenaltyOrCancellationCharge"/>.
    /// </summary>
    public DebitNotePurpose? DebitNotePurpose { get; set; }

    // ---- (2) Vinculo + estado de la ND ----

    /// <summary>
    /// ADR-013 §3.1: FK a la <see cref="Invoice"/> que es la ND emitida por la
    /// penalidad propia. Null hasta que se encola la ND. <b>Es el guard de
    /// idempotencia</b>: si ya tiene valor, NO se crea otra ND (espeja como el BC
    /// vincula la NC via <see cref="CreditNoteInvoiceId"/>).
    /// </summary>
    public int? DebitNoteInvoiceId { get; set; }
    public Invoice? DebitNoteInvoice { get; set; }

    /// <summary>
    /// ADR-013 §3.10 (M4): estado observable de la ND. Alimenta la bandeja
    /// "cancelaciones con NC pero sin su ND". Default
    /// <see cref="Entities.DebitNoteStatus.NotApplicable"/> (conservador = no hay ND).
    /// </summary>
    public DebitNoteStatus DebitNoteStatus { get; set; } = DebitNoteStatus.NotApplicable;

    /// <summary>
    /// ADR-013 §3.10: mensaje de error que ARCA devolvio si la ND fallo su CAE.
    /// Persistido aca para que el back-office vea el motivo sin abrir Hangfire.
    /// </summary>
    [MaxLength(1000)]
    public string? DebitNoteArcaErrorMessage { get; set; }

    // ---- (3) Snapshot fiscal de la ND (congelado al momento del evento, §3.8) ----

    /// <summary>ADR-013 §3.8: monto de la penalidad confirmada congelado al disparar la ND.</summary>
    public decimal? PenaltyAmountAtEvent { get; set; }

    /// <summary>
    /// ADR-013 §3.8: moneda de la penalidad congelada al disparar la ND. Es la moneda en la que se EMITE la
    /// Nota de Debito al cliente (alimenta la emision/FX de la ND). Se deriva del <c>MonId</c> de la factura
    /// original (espacio ARCA): <c>PES</c> se normaliza a <c>ARS</c>, pero el resto conserva el codigo ARCA
    /// tal cual (ej. <c>DOL</c>, NO <c>USD</c>). Ver <c>FreezeDebitNoteSnapshot</c>.
    ///
    /// <para><b>OJO — NO confundir con <see cref="BookingCancellationLine.PenaltyCurrency"/></b>: aquel es la
    /// moneda en que el OPERADOR RETUVO la multa (registro/verdad operativa, espacio ISO 4217 puro: USD/ARS).
    /// Este (<c>PenaltyCurrencyAtEvent</c>) es la moneda de EMISION de la ND (espacio ARCA hibrido: ARS/DOL).
    /// Son conceptos y espacios de codigos DISTINTOS: <b>NO se deben cablear cruzados sin un mapper ISO->ARCA</b>
    /// (ej. USD->DOL). Hoy no se cruzan: <c>PenaltyCurrency</c> es solo captura y <c>PenaltyCurrencyAtEvent</c>
    /// se sigue derivando de la factura, como antes.</para>
    /// </summary>
    [MaxLength(3)]
    public string? PenaltyCurrencyAtEvent { get; set; }

    /// <summary>
    /// ADR-013 §3.8 (M3): tipo de comprobante de la ND derivado del
    /// <c>TipoComprobante</c> de la factura original AL MOMENTO (ej. 12 = ND C), NO
    /// de la condicion fiscal del emisor. Congelado para auditoria.
    /// </summary>
    public int? DebitNoteCbteTipoAtEvent { get; set; }

    /// <summary>
    /// ADR-013 §3.8 (M3): tipo de comprobante de la factura asociada (11/12 = C en el
    /// MVP). Es la FUENTE DE VERDAD para derivar el tipo de la ND.
    /// </summary>
    public int? OriginalInvoiceCbteTipoAtEvent { get; set; }

    /// <summary>
    /// ADR-013 §3.8: condicion fiscal del emisor al momento (Mono en el MVP).
    /// INFORMATIVO/auditoria — NO se usa para derivar el tipo de la ND (eso lo hace
    /// <see cref="OriginalInvoiceCbteTipoAtEvent"/>).
    /// </summary>
    [MaxLength(50)]
    public string? EmitterTaxConditionAtEvent { get; set; }

    /// <summary>ADR-013 §3.8: "quien se queda la penalidad" congelado del operador (Agency/Operator).</summary>
    public PenaltyOwnership? PenaltyOwnershipAtEvent { get; set; }

    /// <summary>ADR-013 §3.8 (R5 + audit): userId que confirmo el monto de la penalidad.</summary>
    [MaxLength(450)]
    public string? PenaltyConfirmedByUserId { get; set; }

    /// <summary>
    /// ADR-013 §3.8 (M1, review 2026-06-01): nombre legible del usuario que confirmo el
    /// monto. Persistido (no solo el Id) por simetria con el resto del modulo de
    /// auditoria fiscal (DraftedByUserName/ConfirmedByUserName/etc.), para que el
    /// back-office vea el rastro sin resolver el Id contra AspNetUsers.
    /// </summary>
    [MaxLength(200)]
    public string? PenaltyConfirmedByUserName { get; set; }

    /// <summary>ADR-013 §3.8: momento UTC en que se confirmo la penalidad.</summary>
    public DateTime? PenaltyConfirmedAt { get; set; }

    /// <summary>
    /// ADR-013 §3.11: userId que clasifico el concepto (pass-through vs ingreso
    /// propio). Es la decision fiscalmente mas sensible -> se audita el clasificador,
    /// no solo el confirmador.
    /// </summary>
    [MaxLength(450)]
    public string? ConceptClassifiedByUserId { get; set; }

    /// <summary>
    /// ADR-013 §3.11 (M1, review 2026-06-01): nombre legible del usuario que clasifico
    /// el concepto. Mismo motivo que <see cref="PenaltyConfirmedByUserName"/>.
    /// </summary>
    [MaxLength(200)]
    public string? ConceptClassifiedByUserName { get; set; }

    /// <summary>ADR-013 §3.11: momento UTC en que se clasifico el concepto.</summary>
    public DateTime? ConceptClassifiedAt { get; set; }

    // ============================================================
    // ADR-014 (2026-06-02): confirmacion DIFERIDA de la penalidad.
    //
    // Cuando el operador confirma el monto de la penalidad DIAS DESPUES de la
    // cancelacion (el caso dominante del negocio), necesitamos persistir dos datos
    // que el flujo sincrono de ADR-013 no tenia: la fecha REAL en que el operador
    // confirmo (eje fiscal del plazo) y la referencia al soporte documental del
    // acuerdo. Ambos aditivos y nullable.
    // ============================================================

    /// <summary>
    /// ADR-014 §3.3: la fecha REAL en que el operador comunico/confirmo el monto de
    /// la penalidad. <b>Distinta</b> de <see cref="PenaltyConfirmedAt"/> (que es el
    /// timestamp del acto en el sistema, "cuando un usuario apreto Confirmar"). El
    /// plazo de gracia RG 4540 (15 dias corridos) corre desde ESTA fecha, no desde
    /// la del sistema. Null mientras la penalidad no se confirmo por el flujo diferido.
    /// </summary>
    public DateTime? OperatorPenaltyConfirmedDate { get; set; }

    /// <summary>
    /// ADR-014 §3.3: referencia/URL del soporte documental del acuerdo del operador
    /// (mail, PDF de confirmacion del monto). Opcional, de auditoria. Si NO se adjunta,
    /// la confirmacion diferida exige 4-eyes (§3.6): confirmar una penalidad propia sin
    /// respaldo es el caso de mayor riesgo fiscal. NO almacena el archivo, solo la
    /// referencia (subir el archivo a MinIO seria una pieza aparte).
    /// </summary>
    [MaxLength(500)]
    public string? SupportingDocumentReference { get; set; }

    // ============================================================
    // ADR-025 (DT.1.1, 2026-06-13): lineas hijas (una por servicio cancelado).
    //
    // La multiplicidad (varios operadores, varios servicios cancelados) vive aca,
    // DEBAJO del BC, no en multiples BC por reserva. El padre conserva la cara
    // fiscal UNICA hacia el cliente (factura/NC); cada linea lleva su operador,
    // moneda, penalidad y circuito de refund. Ver BookingCancellationLine.
    //
    // Los BC mono-operador historicos tienen 1 linea sintetica (backfill Adr028_M1),
    // asi que el path mono-operador es byte-equivalente: 1 linea con bc.SupplierId.
    // ============================================================
    public ICollection<BookingCancellationLine> Lines { get; set; } = new List<BookingCancellationLine>();

    // ============================================================
    // ADR-042 (2026-07-01): hijas (una por factura de venta -> su NC) para el caso
    // multi-factura multimoneda. El caso mono-factura tiene UNA hija que espeja el
    // puntero principal (OriginatingInvoiceId/CreditNoteInvoiceId), byte-equivalente.
    // La completitud de la anulacion (todas OK / parcial / todas fallan) se decide
    // contando estas hijas. Ver BookingCancellationCreditNote.
    // ============================================================
    public ICollection<BookingCancellationCreditNote> CreditNotes { get; set; } = new List<BookingCancellationCreditNote>();
}
