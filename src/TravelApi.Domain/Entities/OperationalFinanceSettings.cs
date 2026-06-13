using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public static class AfipInvoiceControlModes
{
    public const string FullPaymentRequired = "FullPaymentRequired";
    public const string AllowAgentOverrideWithReason = "AllowAgentOverrideWithReason";
}

public class OperationalFinanceSettings
{
    public int Id { get; set; }

    public bool RequireFullPaymentForOperativeStatus { get; set; } = true;
    public bool RequireFullPaymentForVoucher { get; set; } = true;

    [MaxLength(50)]
    public string AfipInvoiceControlMode { get; set; } = AfipInvoiceControlModes.AllowAgentOverrideWithReason;

    public bool EnableUpcomingUnpaidReservationNotifications { get; set; } = true;
    public int UpcomingUnpaidReservationAlertDays { get; set; } = 7;

    /// <summary>
    /// B1.15 Fase 2a (Decision 5 de Gaston): tope de descuento (% sobre precio
    /// de referencia) que un Vendedor puede aplicar sin requerir el permiso
    /// <c>reservas.discount_above_threshold</c>. Default 10%. Rango valido 0..100.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(5,2)")]
    public decimal MaxDiscountPercentWithoutOverride { get; set; } = 10m;

    /// <summary>
    /// B1.15 Fase B' (2026-05-11): dias hasta que una <c>ApprovalRequest</c>
    /// aprobada/pending expira automaticamente. Si pasan, el solicitante debe
    /// re-pedir. Default 7. Configurable por tipo via override.
    /// </summary>
    public int ApprovalDefaultExpirationDays { get; set; } = 7;

    /// <summary>
    /// B1.15 Fase B' (2026-05-11): horas durante las cuales el solicitante NO
    /// puede re-pedir la misma combinacion <c>(RequestType, EntityId)</c> tras
    /// un rechazo. Anti-spam. Default 1 hora.
    /// </summary>
    public int ApprovalRejectionCooldownHours { get; set; } = 1;

    /// <summary>
    /// B1.15 Fase D (2026-05-11): si <c>true</c>, anular factura requiere un
    /// <c>ApprovalRequest</c> aprobado previamente (Vendedor solicita, Admin/
    /// Colaborador aprueba). Admin bypassea este check. Si <c>false</c>,
    /// cualquier user con <c>cobranzas.invoice_annul</c> puede anular directo.
    /// Default <c>true</c> (recomendacion fiscal).
    /// </summary>
    public bool RequireApprovalForInvoiceAnnulment { get; set; } = true;

    // ============================================================
    // FC1.2.0 (plan tactico v3, 2026-05-17): settings del modulo de
    // cancelacion/refund. Todas las columnas agregadas en la migracion
    // FC1_2_0_AddSettingsAndApprovalRequestFK. Defaults pensados para
    // que la habilitacion en prod sea segura (flag OFF, politicas
    // conservadoras).
    // ============================================================

    /// <summary>
    /// FC1.2.0 v3 §10.1 — feature flag maestro del modulo FC1.2.
    /// Si <c>false</c> los services nuevos (BookingCancellationService,
    /// OperatorRefundService, ClientCreditService) rechazan todas las
    /// operaciones con un mensaje "modulo no habilitado". Permite mergear
    /// y deployar la infra sin exponer el flujo a usuarios hasta que QA
    /// y signoff fiscal (OPS-FISCAL-001) lo aprueben.
    /// Default <c>false</c> en prod; QA lo levanta a <c>true</c> en staging.
    /// Precondicion antes de prender en prod: ver query SQL §10.2.1 del plan
    /// (ResponsibleUserId backfill).
    /// </summary>
    public bool EnableNewCancellationFlow { get; set; } = false;

    /// <summary>
    /// FC1.2.0 v3 §10.1 — INV-100 precondicion. Si <c>true</c>, la cancelacion
    /// rechaza reservas con mas de una factura activa (no anulada) para evitar
    /// ambiguedad sobre cual factura anular. Mantenido en <c>true</c> hasta que
    /// FC1.3 modele cancelaciones con multiples facturas (caso real raro pero
    /// existente: invoice A inicial + invoice complementaria por upgrade).
    /// </summary>
    public bool OnePerReservaInvoicePolicy { get; set; } = true;

    /// <summary>
    /// FC1.2.0 v3 §10.1 — dias desde T0 (confirmacion con cliente) hasta que
    /// el job nocturno <c>OperatorRefundTimeoutJob</c> (FC2) mueve el BC a
    /// <c>AbandonedByOperator</c>. Default 60 dias siguiendo la practica
    /// retail de Argentina (mayoria de operadores devuelve dentro de 30-45
    /// dias; 60 deja buffer). Configurable por agencia desde el panel admin.
    /// </summary>
    public int OperatorRefundTimeoutDays { get; set; } = 60;

    /// <summary>
    /// FC1.2.0 v3 §10.1 — Ley 25.345 (FC4): umbral en ARS sobre el cual los
    /// retiros en efectivo (<c>WithdrawalKind.PhysicalCash</c>) requieren validacion
    /// adicional (CUIT, comprobante bancario, etc.). Default 1.000.000 ARS.
    ///
    /// POR QUE es configurable y no una constante (auditoria fiscal 2026-05-29, fuentes
    /// oficiales): la Ley 25.345 existe, pero el monto que figura en su texto original
    /// ($1.000) esta congelado/desactualizado. El umbral OPERATIVO real lo fija ARCA por
    /// resolucion y se actualiza periodicamente. Por eso el tope NO se hardcodea: el admin
    /// lo ajusta desde el panel cuando ARCA emite una nueva resolucion, sin redeploy.
    ///
    /// **Confirmar el valor vigente con contador matriculado** antes de prender en prod.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal Ley25345ThresholdAmount { get; set; } = 1_000_000m;

    /// <summary>
    /// FC1.2.0 v3 §10.1 — umbral en ARS por encima del cual un retiro fisico
    /// dispara una alerta en el panel Admin (auditoria diaria de movimientos
    /// grandes). Default 50.000 ARS. Pensado para que el Admin vea por dashboard
    /// los retiros importantes sin tener que filtrar por monto manualmente.
    /// Es informativo, no bloquea operaciones.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal PhysicalRefundAlertThreshold { get; set; } = 50_000m;

    // ============================================================
    // FC1.3 (ADR-009 §2.3.4, 2026-05-21): settings del modulo NC parcial.
    // Todos defaults conservadores. La habilitacion en prod requiere que
    // el contador firme primero los thresholds y el template. Hasta tanto,
    // el modulo arranca apagado y se prende solo en staging para QA.
    // ============================================================

    /// <summary>
    /// FC1.3 (ADR-009): feature flag maestro del modulo FC1.3. Si <c>false</c>,
    /// el clasificador NO corre y el flujo se comporta exactamente como FC1.2
    /// (NC por total). Default <c>false</c>.
    ///
    /// <para>PRE-CONDICION (GR-002): si este flag es <c>true</c>,
    /// <see cref="EnableNewCancellationFlow"/> tambien tiene que ser <c>true</c>.
    /// La validacion en startup rechaza el arranque si esta combinacion no se
    /// cumple — eso evita prender FC1.3 sin haber firmado primero FC1.2.</para>
    /// </summary>
    public bool EnablePartialCreditNotes { get; set; } = false;

    /// <summary>
    /// FC1.3 (ADR-009): por debajo de este monto en ARS, la NC parcial se
    /// auto-emite si no hay otros disparadores manuales. Default 500.000 ARS.
    /// Sujeto a confirmacion contador (pregunta F1).
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal PartialNcAutoApprovalThreshold { get; set; } = 500_000m;

    /// <summary>
    /// FC1.3 (ADR-009): por encima de <see cref="PartialNcAutoApprovalThreshold"/>
    /// y hasta este monto, requiere admin con comentario minimo 20 chars + 4-eyes
    /// (la persona que aprueba no puede ser la misma que solicito).
    /// Default 2.000.000 ARS.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal PartialNcAdminReviewThreshold { get; set; } = 2_000_000m;

    /// <summary>
    /// FC1.3 (ADR-009 + G5): por encima de <see cref="PartialNcAdminReviewThreshold"/>,
    /// admin reforzada con comentario minimo 100 chars + flag
    /// <c>AccountingReviewRequired=true</c> en el Metadata del approval.
    /// Si es <c>null</c>, no hay tope superior y todo lo mayor a Admin Review entra
    /// al flujo G5.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal? PartialNcAccountingReviewThreshold { get; set; } = null;

    /// <summary>
    /// FC1.3 (ADR-009): template de descripcion de la NC parcial. Variables
    /// soportadas: <c>{invoiceType}</c>, <c>{invoiceNumber}</c>,
    /// <c>{pointOfSale}</c>, <c>{fiscalAmount}</c>, <c>{currency}</c>,
    /// <c>{cancellationReason}</c>, <c>{nonRefundableAmount}</c>,
    /// <c>{operatorPenaltyAmount}</c>, <c>{customerName}</c>,
    /// <c>{customerTaxId}</c>. Validacion al guardar.
    /// </summary>
    [MaxLength(500)]
    public string PartialNcDescriptionTemplate { get; set; } =
        "NC parcial s/Fc {invoiceType} {invoiceNumber} (PV {pointOfSale}). " +
        "Monto fiscal acreditado: {fiscalAmount} {currency}. " +
        "Concepto: {cancellationReason}. " +
        "Items no reintegrables retenidos: {nonRefundableAmount} {currency}.";

    /// <summary>
    /// FC1.3 (ADR-009): dias desde T2 despues de los cuales se alerta al admin
    /// que el plazo RG 4540 (15 dias) esta por vencer en un BC stuck en
    /// <see cref="BookingCancellationStatus.ManualReviewPending"/>. Default 10.
    /// </summary>
    public int ManualReviewMaxDaysBeforeRg4540Alert { get; set; } = 10;

    /// <summary>
    /// FC1.3 (ADR-009 + RH-008): unica expresion regex con alternativas separadas
    /// por '|' que el clasificador caso 4 usa para flagear "factura con descripcion
    /// generica unica".
    ///
    /// <para><b>Default vacio (string.Empty) — heuristica DESACTIVADA</b>
    /// (RH-008/RH-021).</para>
    ///
    /// <para>Configurable por agencia. Si no esta vacio, se evalua case-insensitive
    /// sobre <c>Description</c> del unico <c>InvoiceItem</c>. Activar SOLO si el
    /// contador lo pide explicitamente, despues de testear contra dataset legacy
    /// y confirmar menos del 5% de falsos positivos.</para>
    ///
    /// <para>Ejemplo si activo: <c>"^(servicio|concepto|importe|operacion|reserva)"</c>.</para>
    /// </summary>
    [MaxLength(1000)]
    public string GenericDescriptionPatterns { get; set; } = string.Empty;

    /// <summary>
    /// FC1.3 (ADR-009 + RH-013): timestamp del deploy de FC1.3 a prod.
    /// Heuristica caso 4 (factura confusa): facturas emitidas antes de esta fecha
    /// se flagean como "legacy invoice" para revision manual.
    ///
    /// <para><b>Default null</b> (RH-008): la heuristica legacy esta DESACTIVADA
    /// por default. La migracion M3 setea automaticamente <c>UtcNow</c> solo si
    /// el flag <see cref="EnablePartialCreditNotes"/> ya estaba en <c>true</c> al
    /// momento de aplicar la migracion. Si null + flag en true post-startup, el
    /// validador de startup lo setea a <c>UtcNow</c> y emite un warning.</para>
    /// </summary>
    public DateTime? Fc13DeployDate { get; set; } = null;

    /// <summary>
    /// FC1.3 (ADR-009 + GR-005): si <c>true</c>, permite self-approval del admin
    /// cuando el sistema tiene 1 solo admin y el vendedor que solicito coincide
    /// con ese admin. Requiere comentario reforzado de 100+ chars y un flag
    /// audit <c>SelfApprovedDueToSingleAdmin=true</c> en el Metadata del approval.
    ///
    /// <para>Default <c>false</c> (4-eyes estricto). Pensado para agencias chicas
    /// donde solo hay una persona con rol admin. NO afecta cuando hay 2 o mas
    /// admins activos: en ese caso siempre se exige 4-eyes.</para>
    /// </summary>
    public bool Allow4EyesBypassWhenSingleAdmin { get; set; } = false;

    /// <summary>
    /// FC1.3 (ADR-009 round 3, Q2): cada cuantos minutos el job de reconciliacion
    /// bridge (FC1.3.6b) considera "stale" un <c>ApprovalRequest</c> aprobado pero
    /// con su BC todavia en <see cref="BookingCancellationStatus.ManualReviewPending"/>.
    /// Default 30. El job en si corre cada 30 min via cron fijo; este setting controla
    /// el filtro de antiguedad para no re-disparar callbacks "frescos".
    /// </summary>
    public int BridgeReconciliationStalenessMinutes { get; set; } = 30;

    /// <summary>
    /// FC1.3 (ADR-009 round 3, N-003): umbral de reintentos del job de reconciliacion
    /// bridge antes de declarar el approval como <c>ManualInterventionRequired</c>
    /// y dejar de re-intentar (anti-spam). Default 5.
    ///
    /// <para>Despues de N intentos fallidos consecutivos, el job: (a) NO vuelve a
    /// llamar al bridge, (b) emite UNA notificacion adicional con flag
    /// <c>ManualInterventionRequired=true</c>, (c) requiere force-callback explicito
    /// del admin con InvariantOverride (§2.12 del ADR).</para>
    /// </summary>
    public int BridgeReconciliationMaxRetries { get; set; } = 5;

    // ============================================================
    // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.0, 2026-05-22): 5 settings nuevos
    // del modulo NC parcial REAL (emision contra ARCA). Defaults conservadores:
    // el modulo arranca apagado y se prende manual una vez que QA + contador
    // firman. La validacion de pre-condiciones (Fase 2 depende de Fase 1, dual
    // depende de Fase 2) la hace el startup en Program.cs y el service
    // OperationalFinanceSettingsService.UpdateAsync en runtime.
    // ============================================================

    /// <summary>
    /// FC1.3 Fase 2: feature flag MAESTRO de Fase 2. Si <c>false</c>, el flujo se
    /// comporta como Fase 1 (log warning + emite NC total via path FC1.2). Si
    /// <c>true</c>, FC1.3.3 (BC service) corta el path FC1.2 y empieza a emitir
    /// NC parcial real contra ARCA.
    ///
    /// <para>PRE-CONDICION (validada en startup + service): si este flag es
    /// <c>true</c>, <see cref="EnablePartialCreditNotes"/> tambien tiene que ser
    /// <c>true</c>. Sin Fase 1 (clasificador) no hay liquidacion para emitir.
    /// La validacion rechaza el arranque y rechaza el UPDATE con
    /// ValidationException si esta combinacion no se cumple.</para>
    ///
    /// <para>Default <c>false</c>. El operador prende este flag en staging
    /// despues de pasar QA y en prod despues de signoff del contador.</para>
    /// </summary>
    public bool EnablePartialCreditNoteRealEmission { get; set; } = false;

    /// <summary>
    /// FC1.3 Fase 2: habilita el flow dual "NC total + factura nueva" para los
    /// casos 4 (factura confusa) y 7 (retencion cambia naturaleza). GATED por el
    /// criterio cuantitativo G-F2-A: solo se prende post-prod, una vez medido el
    /// volumen real de estos casos. Hasta tanto, los casos 4 y 7 siguen
    /// rechazando Confirm con <c>InvalidOperationException</c> (GR-001 vigente).
    ///
    /// <para>PRE-CONDICION (validada en startup + service): si este flag es
    /// <c>true</c>, <see cref="EnablePartialCreditNoteRealEmission"/> tambien
    /// tiene que ser <c>true</c>. El flow dual necesita el plumbing de emision
    /// real (no podemos hacer dual sobre un path FC1.2 que solo hace NC total).</para>
    ///
    /// <para>Default <c>false</c>. Se prende solo despues de la auditoria
    /// post-prod G-F2-A si el volumen justifica.</para>
    /// </summary>
    public bool EnableTotalPlusNewInvoiceAutoProcessing { get; set; } = false;

    /// <summary>
    /// FC1.3 Fase 2 (RH-005 / pregunta F1 contador): modo de prorrateo de IVA en
    /// la NC parcial. Default <see cref="IvaProrrateoMode.ProportionalToNet"/>
    /// (criterio conservador del contador pre-respuesta F1). Si el contador
    /// confirma <see cref="IvaProrrateoMode.PerItem"/>, se cambia desde panel
    /// admin sin redeploy.
    /// </summary>
    public IvaProrrateoMode IvaProrrateoMode { get; set; } = IvaProrrateoMode.ProportionalToNet;

    /// <summary>
    /// FC1.3 Fase 2: tolerancia maxima en la validacion defensiva pre-envio al
    /// ARCA: la suma <c>ImpNeto + ImpIVA + ImpTrib</c> debe coincidir con
    /// <c>ImpTotal</c> con error menor o igual a este valor. Si la diferencia es
    /// mayor, throw + log error (NO mandamos XML inconsistente al ARCA).
    ///
    /// <para>Default <c>0.01</c> (un centavo). Expresado en la moneda original
    /// del comprobante — NO necesariamente ARS. Si la factura origen es USD,
    /// la tolerancia se interpreta en USD (1 centavo de dolar).</para>
    ///
    /// <para>Rango razonable: 0.00..1.00. La FluentValidation del DTO valida
    /// este rango (defense-in-depth). Subir mas alla de 1.00 indicaria un bug
    /// de prorrateo, no una tolerancia de redondeo.</para>
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal PartialCreditNoteRoundingTolerance { get; set; } = 0.01m;

    /// <summary>
    /// FC1.3 Fase 2 (RH2-004 + RH4-001 round 4): umbral en MINUTOS para
    /// considerar huerfana una key en la tabla <c>ArcaIdempotencyKeys</c>. Si
    /// una key esta sin resolver mas tiempo que este umbral, probablemente
    /// hubo crash entre el INSERT de la key y el POST al ARCA. El recovery
    /// dispara <c>FECompUltimoAutorizado</c> para verificar si el comprobante
    /// quedo emitido del lado de AFIP a pesar del crash.
    ///
    /// <para>Default <c>10</c> minutos. Rango razonable: 1..60. La
    /// FluentValidation del DTO valida este rango. Mas de 60 atrasaria mucho
    /// el recovery; menos de 1 corre riesgo de declarar "huerfanas" keys que
    /// estan legitimamente en vuelo en ese mismo segundo.</para>
    /// </summary>
    public int IdempotencyKeyStaleThresholdMinutes { get; set; } = 10;

    // ============================================================
    // ADR-012 MVP (facturar en dolares, 2026-05-29): facturacion multimoneda.
    // Hoy la agencia factura SOLO en pesos. Este flag habilita facturar en una
    // moneda extranjera (USD) con tipo de cambio cargado a mano. Defaults
    // conservadores: arranca APAGADO, igual que todos los flags fiscales nuevos.
    // ============================================================

    /// <summary>
    /// ADR-012 MVP (2026-05-29): feature flag MAESTRO de facturacion multimoneda.
    ///
    /// <para><b>Con OFF (default)</b>: la facturacion se comporta EXACTAMENTE como hoy.
    /// El service ignora la moneda que venga en el request y la factura sale en pesos
    /// (PES / cotizacion 1), byte-identica a la facturacion ya homologada con ARCA.
    /// Cero riesgo de regresion fiscal mientras este flag siga apagado.</para>
    ///
    /// <para><b>Con ON</b>: si el request trae una moneda extranjera (MonId != "PES"),
    /// el service exige tipo de cambio coherente + fuente + fecha + justificacion
    /// (TC manual, MVP). Si falta cualquiera de esos datos, rechaza la emision con
    /// una excepcion de validacion clara — NO emite un comprobante a medias.</para>
    ///
    /// <para><b>Por que es ortogonal al tipo de comprobante</b>: la moneda NO cambia
    /// la decision A/B/C (esa la fija la condicion fiscal del cliente/agencia). Una
    /// factura A puede ser en dolares igual que una C. Por eso este flag NO toca la
    /// logica de TipoComprobante en AfipService.</para>
    ///
    /// <para>Default <c>false</c>. Se prende en staging para QA y en prod recien
    /// despues del signoff del contador (TC fiscalmente correcto segun RG 5616) +
    /// homologacion ARCA de un comprobante en moneda extranjera.</para>
    /// </summary>
    public bool EnableMultiCurrencyInvoicing { get; set; } = false;

    // ============================================================
    // ADR-013 (Nota de Debito por penalidad en cancelacion, 2026-06-01): flag
    // maestro de la emision de ND en el flujo de cancelacion. Default conservador
    // (OFF), igual que todos los flags fiscales nuevos.
    // ============================================================

    /// <summary>
    /// ADR-013 (2026-06-01): feature flag MAESTRO de la emision de Nota de Debito por
    /// penalidad propia de la agencia en el flujo de cancelacion.
    ///
    /// <para><b>Con OFF (default)</b>: la cancelacion se comporta EXACTAMENTE como hoy
    /// (NC total, sin ND). El disparo de la ND vive entero detras de este flag. Cero
    /// riesgo de regresion mientras siga apagado.</para>
    ///
    /// <para><b>Con ON</b>: despues de que la NC total obtiene CAE, si el caso pasa el
    /// gating conservador (concepto = ingreso propio de la agencia, penalidad
    /// confirmada, factura original C, moneda ARS, penalidad &lt;= factura), se encola
    /// una ND C asociada a la factura original. Cualquier caso fuera de ese feliz va a
    /// revision manual, NUNCA se emite por las dudas.</para>
    ///
    /// <para><b>NO confundir con <see cref="EnablePartialCreditNoteRealEmission"/></b>
    /// (NC parcial, CONGELADO): ese sigue su camino. Este es un flag NUEVO y distinto.</para>
    ///
    /// <para>Default <c>false</c>. NO prender en prod hasta: (a) signoff del contador
    /// matriculado (§11 del ADR), (b) CAE de homologacion ARCA para ND C asociada a
    /// factura. Misma disciplina que los demas flags fiscales.</para>
    /// </summary>
    public bool EnableCancellationDebitNote { get; set; } = false;

    // ============================================================
    // ADR-014 (Confirmacion DIFERIDA de la penalidad, 2026-06-02): parametros del
    // flujo diferido (confirmar la penalidad dias despues de la cancelacion y emitir
    // la ND en el Dia N). Todos tienen default conservador y SOLO importan cuando
    // EnableCancellationDebitNote esta ON.
    // ============================================================

    /// <summary>
    /// ADR-014 (§3.5): plazo de gracia en dias CORRIDOS desde que el operador confirmo
    /// la penalidad (<c>OperatorPenaltyConfirmedDate</c>). Si al emitir la ND ya pasaron
    /// mas dias que este valor, la ND se emite IGUAL pero se loguea un warning + counter
    /// para que el back-office lo vea (NO bloquea: la validez fiscal de una ND tardia es
    /// decision del contador, no del software). Default 15 dias (RG 4540, a confirmar con
    /// contador matriculado antes de prod).
    /// </summary>
    public int CancellationDebitNoteGraceDays { get; set; } = 15;

    /// <summary>
    /// ADR-014 (§3.5, M4): segundo umbral, mas alto, para priorizar las NDs MUY tardias.
    /// Si pasaron mas dias que este valor desde la confirmacion del operador, el warning
    /// se eleva (counter distinto <c>cancellation_debit_note_very_late</c>) para que el
    /// back-office lo trate con prioridad. Tampoco bloquea. Default 60 dias.
    /// </summary>
    public int CancellationDebitNoteHardWarnDays { get; set; } = 60;

    /// <summary>
    /// ADR-014 (§3.6, M2): umbral en ARS por encima del cual la confirmacion diferida de
    /// la penalidad EXIGE doble firma (4-eyes), aunque haya soporte documental. El 4-eyes
    /// tambien es obligatorio SIEMPRE que NO se adjunte <c>SupportingDocumentReference</c>
    /// (confirmar una penalidad propia sin respaldo documental es el caso de mayor riesgo
    /// fiscal). Por debajo del umbral Y con soporte documental, alcanza el permiso
    /// <c>cancellations.classify_agency_penalty</c>. Default 2.000.000 ARS (espeja el
    /// umbral de revision admin de FC1.3).
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal CancellationDebitNoteFourEyesThreshold { get; set; } = 2_000_000m;

    // ============================================================
    // ADR-016 F0a (Base del copiloto de IA, 2026-06-03): flag MAESTRO del copiloto.
    // Default conservador (OFF), igual que todos los flags nuevos. En F0a este flag
    // NO tiene caller todavia (el cerebro existe pero nadie lo invoca); el piloto que
    // lo consume llega en F1. Por eso aca NO hay validacion cruzada: el flag del piloto
    // (EnableAiUpcomingClientAlerts) todavia no existe.
    // ============================================================

    /// <summary>
    /// ADR-016 F0a (2026-06-03): feature flag MAESTRO del copiloto de IA.
    ///
    /// <para><b>Con OFF (default)</b>: el copiloto no existe. El cerebro de IA esta registrado
    /// en DI pero NADIE lo invoca, asi que el comportamiento es byte-identico a hoy y CERO
    /// datos salen del sistema hacia la nube. Es la posicion segura.</para>
    ///
    /// <para><b>Con ON</b>: habilita que los modulos que se enchufen al cerebro (a partir de F1:
    /// el enriquecimiento de alertas "cliente por vencer") puedan llamar a la IA. El primer
    /// caller real NO se construye en F0a.</para>
    ///
    /// <para><b>Config aparte (env)</b>: la conexion al proveedor (base_url, API key, modelo)
    /// vive en variables de entorno (<c>Ai__*</c>), NO en esta tabla. La API key es un secreto
    /// y nunca va a la DB. Prender este flag sin esa config hace que el cerebro degrade elegante
    /// (no rompe nada, solo no genera texto IA).</para>
    ///
    /// <para>Default <c>false</c>. Editable desde el panel admin (PUT operational-finance) y
    /// expuesto read-only en <c>GET /afip/settings</c>, igual que los demas flags.</para>
    /// </summary>
    public bool EnableAiCopilot { get; set; } = false;

    // ============================================================
    // ADR-017 F1.1 (catalogo find-or-create + fechas limite, 2026-06-05): 2 flags + 1 setting.
    // Defaults conservadores (flags OFF). En F1.1 NADIE los lee todavia (no hay comportamiento
    // condicionado aun); solo existen, persisten y se togglean desde el panel. El comportamiento
    // que gobiernan se construye en F1.2+ (catalog-search, request-manda, upsert) y F3 (alertas).
    // ============================================================

    /// <summary>
    /// ADR-017 (2026-06-05): feature flag MAESTRO del catalogo find-or-create desde la venta.
    ///
    /// <para><b>Con OFF (default)</b>: byte-identico a hoy. Los endpoints nuevos (catalog-search) dan
    /// 404, los requests con producto nuevo dan 400, el snapshot actual pisa como hoy, no hay
    /// transaccion nueva ni upsert de RateSupplierSale, y la ficha inline del front no se monta.</para>
    ///
    /// <para><b>Con ON</b> (a partir de F1.2+): habilita el buscador find-or-create, la creacion inline
    /// del producto, la regla "request manda", la cadena de costo D7 (marca "costo a confirmar") y el
    /// upsert de RateSupplierSale. Es un flag de COMPORTAMIENTO puro, sin emision fiscal y sin
    /// dependencias con otros flags, por eso NO tiene validacion cruzada.</para>
    ///
    /// <para>Default <c>false</c>. Editable desde el panel admin (PUT operational-finance).</para>
    /// </summary>
    public bool EnableCatalogFindOrCreate { get; set; } = false;

    /// <summary>
    /// Feature flag de los avisos "Proximos inicios" (UI: "Proximos inicios" — ADR-019, que reemplazo
    /// a las fechas limite manuales de ADR-017 F1.4, nunca prendidas en prod). El nombre interno NO se
    /// renombro a proposito (decision D7 del ADR: renombrar = migracion + churn por cero valor de
    /// usuario). Independiente de <see cref="EnableCatalogFindOrCreate"/>.
    ///
    /// <para><b>Con OFF (default)</b>: <c>/alerts</c> byte-identico al historico y el endpoint de
    /// dismiss devuelve 404. <b>Con ON</b>: aparece el bucket <c>upcomingStarts</c> (un aviso POR
    /// RESERVA vendida/confirmada cuyo primer servicio empieza dentro de la ventana) +
    /// <c>upcomingStartsWindowDays</c>, y cada vendedor ve los avisos de SUS reservas.</para>
    ///
    /// <para>Default <c>false</c>. Editable desde el panel admin.</para>
    /// </summary>
    public bool EnableServiceDeadlineAlerts { get; set; } = false;

    /// <summary>
    /// ADR-017 (decision D7, 2026-06-05): umbral en DIAS para considerar "vieja" una referencia de costo
    /// usada por la cadena D7 (F1.3). Si el costo de referencia es mas viejo que esto, el servicio se
    /// marca "costo a confirmar" para que alguien con permiso lo revise. En F1.1 nadie lo lee todavia.
    /// Default 60 (mismo orden que <see cref="OperatorRefundTimeoutDays"/>). Editable desde el panel.
    /// </summary>
    public int StaleCostReferenceDays { get; set; } = 60;

    /// <summary>
    /// Ventana en DIAS del aviso "Proximos inicios" (UI: "Dias de anticipacion del aviso" — ADR-019;
    /// el nombre interno conserva el de ADR-017 F1.4 a proposito, ver
    /// <see cref="EnableServiceDeadlineAlerts"/>). Una reserva avisa cuando su primer servicio empieza
    /// dentro de <c>[hoy ... hoy + ServiceDeadlineAlertDays]</c> (bordes inclusivos; "hoy" en pared
    /// Argentina). No hay estado "vencido": pasado el inicio el aviso desaparece solo. Default 7
    /// (mismo orden que <see cref="UpcomingUnpaidReservationAlertDays"/>). Solo importa con el flag
    /// ON; editable desde el panel admin y expuesto al front como <c>upcomingStartsWindowDays</c>
    /// dentro del payload de <c>/alerts</c>.
    /// </summary>
    public int ServiceDeadlineAlertDays { get; set; } = 7;

    // ============================================================
    // Auditoria ERP 2026-06-12 (hallazgo #1): comision del vendedor. INTERRUPTOR de negocio (NO un feature
    // flag de los prohibidos): la funcion va completa y el dueño decide si la usa desde Configuracion.
    // ============================================================

    /// <summary>
    /// Auditoria ERP 2026-06-12 (hallazgo #1, decision del dueño): interruptor maestro de la comision del
    /// vendedor.
    ///
    /// <para><b>Con OFF (default)</b>: byte-identico a antes de esta feature. El persister de comisiones es
    /// un no-op total: no calcula ni escribe ninguna fila de <c>CommissionAccrual</c>. Cero devengo.</para>
    ///
    /// <para><b>Con ON</b>: cuando una reserva queda totalmente cobrada (<c>Balance &lt;= 0</c>), se devenga
    /// la comision del vendedor responsable como un % (de <c>CommissionRule</c>) sobre la GANANCIA de los
    /// servicios confirmados, separada por moneda (ADR-021). Si la reserva se cancela o el saldo vuelve a
    /// positivo, la comision devengada vuelve a 0 (tope cero). Sin regla aplicable -> 0 (no se inventa %).</para>
    ///
    /// <para>Es un ajuste de NEGOCIO puro, sin dependencias con otros flags, por eso NO tiene validacion
    /// cruzada. Editable por Admin desde el panel (PUT operational-finance) y expuesto read-only en el GET.
    /// Default <c>false</c>: el dueño lo prende cuando quiera.</para>
    /// </summary>
    public bool EnableSellerCommissions { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
