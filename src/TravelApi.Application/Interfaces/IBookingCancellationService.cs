using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.2.1 v3 §2.1 (2026-05-17): orquestador del flujo de cancelacion de
/// reservas (T-1 / T0 / T2 / T3 — ver ADR-002 §2.4).
///
/// <para>
/// <b>Responsabilidad</b>: gestionar el ciclo de vida del <c>BookingCancellation</c>
/// aggregate root, coordinando con <c>InvoiceService</c> (NC fiscal),
/// <c>ApprovalRequestService</c> (overrides) y los services de refund/credit
/// (cuando lleguen en FC1.2.2/.2.3). El service NO ejecuta operaciones AFIP
/// directamente: las delega via <see cref="IInvoiceService.EnqueueAnnulmentAsync"/>.
/// </para>
///
/// <para>
/// <b>Maquina de estados</b> (resumen, ver <c>BookingCancellationStatus</c>):
/// <list type="bullet">
/// <item><c>Drafted</c> ← <see cref="DraftAsync"/></item>
/// <item><c>Drafted</c> → <c>AwaitingFiscalConfirmation</c> via <see cref="ConfirmAsync"/></item>
/// <item><c>Drafted</c> → <c>Aborted</c> via <see cref="AbortAsync"/></item>
/// <item><c>AwaitingFiscalConfirmation</c> → <c>AwaitingOperatorRefund</c> via callback
///       <see cref="IInvoiceAnnulmentBcBridge.OnArcaSucceededAsync"/> (Hangfire)</item>
/// <item><c>AwaitingFiscalConfirmation</c> → <c>AwaitingOperatorRefund</c> manual via
///       <see cref="ForceArcaConfirmationAsync"/> (admin escape hatch BR-V2-01)</item>
/// <item><c>AwaitingFiscalConfirmation</c> → <c>ArcaRejected</c> via callback
///       <see cref="IInvoiceAnnulmentBcBridge.OnArcaFailedAsync"/></item>
/// </list>
/// </para>
///
/// <para>
/// <b>FC1.2.1 alcance</b>: implementamos Draft/Confirm/Abort/ForceArca + bridge.
/// Los hooks <c>OnAllocationRecorded</c>/<c>OnAllCreditConsumed</c> se exponen en
/// la interface pero la implementacion completa de las transiciones T2/T3 llega
/// en FC1.2.2 y FC1.2.3. Hoy quedan stubs documentados.
/// </para>
/// </summary>
public interface IBookingCancellationService
{
    // ===== Comandos (UI) =====

    /// <summary>
    /// T-1: crea el BC en <c>Drafted</c>. Valida INV-081 (una sola cancelacion
    /// activa por reserva) y INV-100 (<c>OnePerReservaInvoicePolicy</c>).
    /// FiscalSnapshot queda vacio: <see cref="ConfirmAsync"/> lo completa al
    /// disparar T0.
    /// </summary>
    Task<BookingCancellationDto> DraftAsync(
        DraftCancellationRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// T0: completa el FiscalSnapshot, encola la NC en AFIP y transiciona el BC
    /// a <c>AwaitingFiscalConfirmation</c>. Setea la Reserva en
    /// <c>PendingOperatorRefund</c>. Si el caller es Admin y declara
    /// <c>IsAdminOverride=true</c>, requiere un <c>InvariantOverride</c> aprobado.
    /// Throws <c>ApprovalRequiredException</c> sino.
    /// </summary>
    /// <param name="userCanClassifyAgencyPenalty">
    /// ADR-013: true si el caller tiene el permiso
    /// <c>cancellations.classify_agency_penalty</c> (o es Admin). Lo resuelve el
    /// controller contra los claims del usuario. El service lo exige cuando la
    /// clasificacion del request es "ingreso propio de la agencia" (dispara ND
    /// fiscal): un vendedor comun NO puede disparar una ND.
    /// </param>
    Task<BookingCancellationDto> ConfirmAsync(
        Guid publicId,
        ConfirmCancellationRequest request,
        string userId,
        string? userName,
        bool requesterIsAdmin,
        CancellationToken ct,
        // ADR-013: lo ponemos DESPUES del CancellationToken con default false para no
        // romper los callers posicionales existentes (pasan el token como ultimo arg) y
        // mantener conservador por default. El controller lo pasa nombrado con el valor
        // real resuelto contra el permiso. NUNCA defaultear a true (abriria la ND a
        // cualquier caller).
        bool userCanClassifyAgencyPenalty = false);

    /// <summary>
    /// ADR-044 T5-emision (2026-07-15, diseño §6.1): confirma y EMITE la Nota de Credito real de una
    /// cancelacion PARCIAL (se canceló UN servicio de una reserva facturada, la factura sigue viva por el
    /// resto). Sella el <c>FiscalSnapshot</c> (heredado de la factura destino: moneda/TC congelados, NUNCA se
    /// recotiza), transiciona el BC <c>Drafted → AwaitingFiscalConfirmation</c> y emite la NC via el pipeline
    /// de bajo nivel (<c>InvoiceService.CreateAsync</c> + <c>ProcessInvoiceJob</c>) — NUNCA el pipeline legacy
    /// que marca la factura de venta <c>AnnulmentStatus=Succeeded</c> (eso mataria la factura para el resto de
    /// servicios). La reconciliacion al CAE la hace un reconciliador T5 DEDICADO (nunca
    /// <c>OnArcaSucceededAsync</c>, que dispararia una anulacion TOTAL fantasma).
    ///
    /// <para><b>Guards duros (400/409)</b>: BC debe existir y estar <c>Drafted</c>, puramente parcial (≥1 linea
    /// <c>Scope=Partial</c>, ninguna <c>Scope=Full</c>); tiene que haber AL MENOS UNA linea Partial con factura
    /// destino y monto resueltos y sin devolucion emitida todavia (<c>INV-T5-EMIT-UNRESOLVED</c> si no); el
    /// monto no puede exceder el remanente FRESCO de la factura destino, releido bajo el lock por factura
    /// (<c>INV-T5-EMIT-CAP</c> si otra emision lo consumio mientras tanto); si la agencia es Responsable
    /// Inscripto, la emision automatica queda bloqueada (<c>INV-T5-EMIT-RI-SIGNOFF</c>) hasta la firma de un
    /// contador matriculado sobre la alicuota (fiscal, riesgo residual 1) — para Monotributo (la condicion de
    /// hoy) esto NO bloquea.</para>
    ///
    /// <para>El permiso de emision fiscal (mismo que anular-total, <c>cobranzas.invoice_annul</c>) y la
    /// ownership de la reserva los resuelve el controller server-side ANTES de llamar a este metodo.</para>
    ///
    /// <para><b>Spec UX 2026-07-17 (T5 varios pendientes)</b>: <paramref name="request"/> es opcional. Si la
    /// cancelacion tiene VARIAS lineas Partial resueltas contra facturas DISTINTAS (el caso real de Gastón:
    /// hotel USD + excursion ARS), cada devolucion se emite POR SEPARADO — el caller indica
    /// <c>TargetInvoicePublicId</c> para decir cuál. Sin indicarlo, se mantiene el comportamiento de siempre:
    /// si hay una sola factura pendiente de emitir, se emite esa (<c>INV-T5-EMIT-MULTI-INVOICE</c> si hay 2+ y
    /// no se especifica cuál).</para>
    ///
    /// <para><b>Borde D1 (review 2026-07-17, mitigacion liviana)</b>: si esta cancelacion tiene OTRA linea
    /// Partial todavia SIN resolver (sin factura destino elegida) cuya moneda coincide con la de la factura
    /// que se esta por emitir, la emision se rechaza con <c>INV-T5-EMIT-SIBLING-UNRESOLVED</c>. Motivo: la
    /// hija (NC) es unica por factura+BC — si esta NC queda emitida, el guard de resolver
    /// (<c>INV-T5-RESOLVE-FISCAL</c>) va a impedir despues que esa linea hermana se resuelva contra la MISMA
    /// factura, dejandola sin ninguna factura donde emitir su devolucion. Por eso se pide resolver primero
    /// lo que falta de la misma moneda.</para>
    /// </summary>
    Task<BookingCancellationDto> ConfirmPartialCancellationEmissionAsync(
        Guid publicId,
        string userId,
        string? userName,
        CancellationToken ct,
        EmitPartialCreditNoteRequest? request = null);

    /// <summary>
    /// Resuelve factura y monto de una línea T5 legacy (una devolución vieja de un servicio cancelado)
    /// todavía no emitida. Spec UX 2026-07-17: cuando hay VARIAS pendientes al mismo tiempo, el request debe
    /// indicar <c>BookingCancellationLinePublicId</c> — las demás quedan intactas.
    /// </summary>
    Task<BookingCancellationDto> ResolvePartialCreditNoteAsync(
        Guid publicId,
        ResolvePartialCreditNoteRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// Aborta un BC en <c>Drafted</c>. Idempotente: si ya esta <c>Aborted</c>,
    /// retorna el DTO actual sin tocar nada. Si el BC esta en cualquier otro
    /// estado, throws (transicion invalida — usar el flujo normal o
    /// <see cref="ForceArcaConfirmationAsync"/> segun corresponda).
    ///
    /// <para><b>DECISIÓN DEL DUEÑO (2026-07-17)</b>: bloqueado ADEMAS (<c>INV-T5-ABORT-ALREADY-EMITTED</c>) si
    /// alguna hija <see cref="BookingCancellationCreditNote"/> ya esta <c>Succeeded</c>, aunque el BC siga
    /// <c>Drafted</c> (caso T5 varios pendientes: el BC se queda Drafted mientras falte emitir OTRO servicio).
    /// Abortar el evento entero borraria el registro que explica una NC que ya salio con CAE real.</para>
    /// </summary>
    Task<BookingCancellationDto> AbortAsync(
        Guid publicId,
        string reason,
        string userId,
        CancellationToken ct);

    /// <summary>
    /// FC1.2.1 v3 (BR-V2-01): escape hatch admin. Cuando AFIP devolvio CAE para
    /// la NC pero el callback automatico (<c>OnArcaSucceededAsync</c>) fallo
    /// (job zombie, exception no recuperable, etc.), un Admin puede empatar
    /// manualmente el estado del BC con la realidad fiscal.
    ///
    /// <para>
    /// Requiere <c>InvariantOverride</c> aprobado scoped al BC.
    /// Idempotente: si el BC ya esta en <c>AwaitingOperatorRefund</c> o adelante,
    /// retorna no-op + log warning + DTO actual (HTTP 200, no error).
    /// </para>
    /// </summary>
    Task<BookingCancellationDto> ForceArcaConfirmationAsync(
        Guid publicId,
        ForceArcaConfirmationRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// ADR-025 (DT.3.1, 2026-06-13): cancela UN servicio dentro de una reserva, dejando el resto del file
    /// vivo (cancelacion PARCIAL). Decisiones selladas: NO mueve el estado de la reserva (#1); el saldo del
    /// cliente baja solo (el servicio cancelado sale del calculo por ServiceResolutionRules, ADR-020); la
    /// deuda del operador de ESE servicio baja en la MISMA transaccion (B1, reusa SupplierDebtPersister).
    ///
    /// <para><b>NO emite NC automatica</b> (decision #3): el calculo de la NC parcial queda en revision
    /// manual hasta la firma del contador (Q-F2). Este metodo marca el servicio + recalcula plata; el armado
    /// del borrador fiscal y la emision manual son piezas separadas.</para>
    ///
    /// <para><b>Seguridad</b>: valida server-side que el servicio pertenece a la reserva (no confia en el
    /// frontend, espejo de INV-151).</para>
    ///
    /// <para><b>Idempotencia</b>: si el servicio ya esta cancelado, es no-op (devuelve el contador actual).</para>
    /// </summary>
    Task<CancelServiceResultDto> CancelServiceAsync(
        CancelServiceRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// R1 — VARIANTE TOTAL (plata viva, 2026-06-30): guarda que <c>ReservaService.AnnulWithPaymentsToCreditAsync</c>
    /// ("Anular con saldo a favor", sin Nota de Crédito) invoca ANTES de mutar nada. Bloquea la anulación cuando se le
    /// pagó al operador por uno o más servicios y la reserva todavía NO tiene factura de venta viva que ancle el
    /// receivable "me tiene que devolver".
    ///
    /// <para><b>Por qué</b>: ese flujo cancela todos los servicios vivos (la caja del operador queda negativa) sin
    /// crear líneas de cancelación; como el receivable se deriva de esas líneas, sin ellas el reconciler del saldo a
    /// favor materializaría el negativo como crédito GASTABLE (plata que el operador debe devolver). Es la gemela del
    /// candado que ya protege la cancelación de UN servicio suelto.</para>
    ///
    /// <para><b>No bloquea</b> los casos sin fuga: reserva con factura viva (el path normal ancla el receivable),
    /// servicios impagos al operador, o reserva sin ningún servicio con operador. Lanza
    /// <c>InvalidOperationException</c> (el controller la mapea a 409) solo cuando hay plata pagada al operador sin
    /// factura que la ancle. Es READ-ONLY: no persiste nada.</para>
    /// </summary>
    Task EnsureReservaAnnulHasReceivableAnchorAsync(int reservaId, CancellationToken ct);

    /// <summary>
    /// Plata viva (familia R1): impide REASIGNAR el operador (o cambiar la moneda) de UN servicio ya pagado al
    /// operador saliente cuando NO hay factura viva que ancle el receivable. Reasignar el operador (o mover el
    /// servicio a otra moneda) hace desaparecer su compra confirmada del bucket del operador saliente, dejando su
    /// caja en negativo por lo pagado; como ese cambio NO crea ninguna línea de cancelación, el reconciler del saldo
    /// a favor materializaría ese negativo como crédito GASTABLE (plata que el operador debe devolver).
    ///
    /// <para>Usa el MISMO criterio PRECISO que <see cref="EnsureReservaAnnulHasReceivableAnchorAsync"/> y que el
    /// candado de cancelar un servicio suelto: reconstruye el <c>RefundCap</c> del servicio (lo pagado al operador
    /// IMPUTADO A ESTA RESERVA, topeado por el costo del servicio). El pool EXCLUYE el prepago "a cuenta"
    /// (pagos sin reserva imputada), así que un saldo a favor on-account del operador NO dispara este candado.
    /// Lanza <c>InvalidOperationException</c> (el controller la mapea a 409) solo cuando el cap resultante es &gt; 0;
    /// no bloquea si hay factura viva, si el servicio está impago al operador, o si no tiene operador. READ-ONLY.</para>
    ///
    /// <para>El caller debe invocarlo SOLO cuando efectivamente cambió el operador o la moneda y el servicio venía
    /// contando como compra confirmada del operador saliente (si no, moverlo no baja su caja y no hay fuga).</para>
    /// </summary>
    Task EnsureServiceOperatorOrCurrencyChangeHasReceivableAnchorAsync(
        int reservaId, CancellableServiceTable serviceTable, int serviceId, bool isCurrencyChange, CancellationToken ct);

    /// <summary>
    /// P1 "circuito proveedor" (2026-07-21): impide BAJAR el estado de un servicio de Confirmado a
    /// no-confirmado (ej. "des-confirmar" desde la ficha de edición) cuando ese servicio tiene plata
    /// pagada al operador y la reserva NO tiene factura de venta viva que ancle el reembolso. Es la
    /// CUARTA cara de la familia R1, junto con "anular un servicio suelto" (<c>CancelServiceAsync</c>),
    /// <see cref="EnsureReservaAnnulHasReceivableAnchorAsync"/> (anular la reserva entera) y
    /// <see cref="EnsureServiceOperatorOrCurrencyChangeHasReceivableAnchorAsync"/> (reasignar operador/
    /// moneda): las cuatro reconstruyen el mismo <c>RefundCap</c> del servicio (lo pagado al operador
    /// IMPUTADO A ESTA RESERVA, topeado por su costo) y solo bloquean si da &gt; 0.
    ///
    /// <para><b>Por qué existe (hallazgo de Gaston, 2026-07-21)</b>: antes de esta tanda, "bajar el
    /// estado" tenía su PROPIA regla, más ancha e imprecisa (miraba el total de pagos de TODA la
    /// reserva, no de este servicio, y nunca miraba si ya había factura viva) — mismo riesgo de plata
    /// que "anular servicio", pero con una instrucción CONTRADICTORIA. Ahora comparten predicado y
    /// mensaje.</para>
    ///
    /// <para>El caller (<c>BookingService</c>) debe invocarlo SOLO cuando
    /// <c>ReservaCapacityRules.IsStatusDowngradeFromConfirmed</c> da true, para no pagar el costo de
    /// reconstruir el RefundCap en cambios de estado que no son una bajada. Lanza
    /// <c>ServiceCancellationRejectedException</c> (409, mismo <c>Code</c> que "anular servicio") solo
    /// cuando hay plata sin ancla; no bloquea con factura viva, servicio impago, o sin operador.
    /// READ-ONLY: no persiste nada.</para>
    /// </summary>
    Task EnsureServiceStatusDowngradeHasReceivableAnchorAsync(
        int reservaId, CancellableServiceTable serviceTable, int serviceId, CancellationToken ct);

    /// <summary>
    /// Tanda 7 (plan "contrato pantalla-motor", 2026-07-20): pre-chequeo READ-ONLY para el GET de la ficha
    /// de la reserva. Le dice a <c>ReservaService.GetReservaByIdAsync</c>, SIN mutar nada, que servicios
    /// quedarian bloqueados por el candado R1 (plata pagada al operador sin factura) si se intentara anular
    /// AHORA, y si la reserva esta en el caso "factura viva sin cliente asignado".
    ///
    /// <para><b>Short-circuit obligatorio (B1 del review de arquitectura)</b>: primero se pregunta UNA sola
    /// vez si la reserva tiene factura de venta viva (mismo predicado exacto que ancla R1,
    /// <c>ReservaHasLiveSaleInvoiceAsync</c>). Con factura viva, R1 NUNCA bloquea a nadie (el ancla ya
    /// existe) — se devuelve el conjunto vacio SIN reconstruir ninguna linea. Solo en el caso SIN factura se
    /// juntan, en UN solo batch para TODOS los servicios de la reserva (no un query por servicio), los
    /// candidatos con operador vivo.</para>
    ///
    /// <para><b>Cada candidato se evalua AISLADO contra el pool completo (fix post-review, 2026-07-20)</b>:
    /// a diferencia del reparto GREEDY que usa el flujo real de anular la reserva ENTERA (donde todas las
    /// lineas compiten de verdad por el mismo pool al mismo tiempo), este pre-chequeo topea cada servicio
    /// contra el pool disponible COMPLETO (solo reducido por lineas REALES ya persistidas) — exactamente lo
    /// que veria el guard real de UN servicio suelto si se lo evaluara aislado. Con 2+ servicios del mismo
    /// operador+moneda y pool insuficiente para todos, esto puede marcar a MAS de un servicio como
    /// bloqueado (nunca menos de los que el guard real rechazaria). Detalle completo en el XML-doc de
    /// <c>BookingCancellationService.GetServiceCancellationPreflightAsync</c> (Infrastructure).</para>
    /// </summary>
    Task<ServiceCancellationPreflightResult> GetServiceCancellationPreflightAsync(int reservaId, CancellationToken ct);

    // ===== Reacciones internas (llamadas desde otros services del modulo) =====

    /// <summary>
    /// FC1.2.2 trigger: <c>OperatorRefundService.AllocateAsync</c> avisa que
    /// hubo un registro de allocation contra este BC. Si era el primero,
    /// transiciona <c>AwaitingOperatorRefund</c> → <c>ClientCreditApplied</c>.
    ///
    /// <para>
    /// <b>FC1.2.1</b>: stub no-op (implementacion real en FC1.2.2).
    /// </para>
    /// </summary>
    Task OnAllocationRecordedAsync(int bookingCancellationId, decimal netAmount, CancellationToken ct);

    /// <summary>
    /// FC1.2.2 (2026-05-18) trigger: <c>OperatorRefundService.VoidAllocationAsync</c>
    /// avisa que una allocation existente fue anulada (soft-void). Si el BC se
    /// quedo sin allocations activas, hay que volver a
    /// <c>AwaitingOperatorRefund</c> (estaba en <c>ClientCreditApplied</c>).
    ///
    /// <para>
    /// <b>FC1.2.2</b>: implementado en este service. El caller pasa
    /// <c>bookingCancellationId</c> y el <c>netAmount</c> que se libera (mismo
    /// valor que recibio antes en <see cref="OnAllocationRecordedAsync"/>).
    /// </para>
    /// </summary>
    Task OnAllocationVoidedAsync(int bookingCancellationId, decimal netAmount, CancellationToken ct);

    /// <summary>
    /// FC1.2.3 trigger: <c>ClientCreditService</c> avisa que todos los entries del
    /// BC tienen RemainingBalance=0. Cierra el BC (<c>Closed</c>) + cierra la
    /// Reserva (<c>Cancelled</c>).
    ///
    /// <para>
    /// <b>FC1.2.1</b>: stub no-op.
    /// </para>
    /// </summary>
    Task OnAllCreditConsumedAsync(int bookingCancellationId, CancellationToken ct);

    /// <summary>
    /// (2026-06-26) Cierra el ciclo del reembolso del operador: busca las cancelaciones en
    /// <c>AwaitingOperatorRefund</c> cuyo plazo (<c>OperatorRefundDueBy</c>) ya vencio y las transiciona a
    /// <c>AbandonedByOperator</c>, cerrando la RESERVA (<c>PendingOperatorRefund</c> -> <c>Cancelled</c>).
    ///
    /// <para>Antes de este fix el estado <c>AbandonedByOperator</c> NUNCA se asignaba (codigo muerto) y no habia
    /// job que mirara <c>OperatorRefundDueBy</c>: cuando el operador no devolvia el reembolso, la cuenta por
    /// cobrar quedaba colgada para siempre sin alerta. Lo invoca un job nocturno (<c>OperatorRefundTimeoutJob</c>).</para>
    ///
    /// <para><b>Idempotente</b>: re-ejecutarlo no reprocesa (una cancelacion ya en <c>AbandonedByOperator</c> ya
    /// no esta en <c>AwaitingOperatorRefund</c>). <b>Aisla fila veneno</b>: cada cancelacion se procesa y
    /// persiste por separado; si una falla, las demas siguen. Deja rastro de auditoria
    /// (<c>BookingCancellationAbandonedByOperator</c>) y log de cambio de estado de la reserva. Devuelve cuantas
    /// cancelaciones se marcaron como abandonadas.</para>
    ///
    /// <para>(2026-07-03) En la MISMA corrida invoca ademas <see cref="CloseZeroReceivableCancellationsAsync"/>
    /// para cerrar las anulaciones trabadas sin reembolso pendiente del operador (ver ese metodo). El valor que
    /// devuelve sigue siendo SOLO la cantidad de abandonadas por timeout (no incluye las cerradas por $0).</para>
    /// </summary>
    Task<int> ProcessExpiredOperatorRefundsAsync(CancellationToken ct);

    /// <summary>
    /// (2026-07-03) Cierra las anulaciones que quedaron trabadas "esperando reembolso" cuando el operador NO tiene
    /// nada que devolver (receivable vivo $0 en TODAS las monedas — tipico cuando la agencia nunca le pago nada al
    /// operador por ese viaje). Candidatas: las que estan en <c>AwaitingOperatorRefund</c> o
    /// <c>AbandonedByOperator</c>; las que cumplen la regla pasan a <c>Closed</c> y su RESERVA
    /// <c>PendingOperatorRefund</c> -> <c>Cancelled</c>.
    ///
    /// <para><b>No cierra</b> si el operador todavia debe algo (receivable &gt; 0) ni si la multa del operador sigue
    /// pendiente de gestion (su Nota de Debito puede tener que emitirse primero). NO toca comprobantes fiscales.</para>
    ///
    /// <para><b>Idempotente</b> (re-ejecutarla no reprocesa: una ya <c>Closed</c> deja de ser candidata) y
    /// <b>defensivo</b> (aisla fila veneno: cada cierre en su propio SaveChanges; si uno falla, los demas siguen).
    /// Deja auditoria <c>BookingCancellationClosedNoOperatorRefundDue</c> + log de cambio de estado por cada una.
    /// Lo invoca el job nocturno de timeouts (misma corrida que <see cref="ProcessExpiredOperatorRefundsAsync"/>).
    /// Devuelve cuantas se cerraron.</para>
    /// </summary>
    Task<int> CloseZeroReceivableCancellationsAsync(CancellationToken ct);

    /// <summary>
    /// FIX B (2026-07-04): RED DE SEGURIDAD para el aviso de AFIP perdido en la NC TOTAL. La transicion
    /// <c>AwaitingFiscalConfirmation</c> -&gt; <c>AwaitingOperatorRefund</c> depende 100% del callback de Hangfire
    /// (<c>OnArcaSucceededAsync</c>/<c>OnArcaFailedAsync</c>) que dispara <c>ProcessAnnulmentJob</c> al terminar
    /// AFIP. Si la NC obtiene CAE (o es rechazada) pero ese callback muere de forma permanente, la cancelacion
    /// queda trabada en <c>AwaitingFiscalConfirmation</c> y la reserva en <c>PendingOperatorRefund</c>, INVISIBLE a
    /// todos los barridos y alertas (filtran Awaiting/Abandoned). Es el equivalente TOTAL del
    /// <c>PartialCreditNoteBridgeReconciliationJob</c> que ya existe para la NC parcial.
    ///
    /// <para>Busca cancelaciones en <c>AwaitingFiscalConfirmation</c> mas viejas que un umbral prudente (reusa
    /// <c>BridgeReconciliationStalenessMinutes</c>, el mismo del job parcial) cuya(s) Invoice de NC ya tiene
    /// resultado FINAL de AFIP (aprobado "A" con CAE, o rechazo "R") y RE-INVOCA el MISMO callback del bridge que
    /// habria corrido (no duplica logica). El callback es idempotente; al transicionar, la BC puede auto-cerrarse
    /// de una si no hubo plata al operador (correcto y deseado, misma logica que el flujo normal).</para>
    ///
    /// <para><b>Idempotente</b> (una BC que ya salio de <c>AwaitingFiscalConfirmation</c> deja de ser candidata;
    /// correr dos veces no duplica NC ni ND — el callback re-chequea el estado) y <b>defensivo</b> (aisla fila
    /// veneno: cada BC en su propio intento; si una falla, las demas siguen). Lo invoca un recurring job
    /// (<c>TotalCreditNoteBridgeReconciliationJob</c>). Devuelve cuantas cancelaciones se reconciliaron.</para>
    /// </summary>
    Task<int> ReconcileStuckFiscalConfirmationsAsync(CancellationToken ct);

    /// <summary>
    /// ADR-041 TANDA 4 (2026-06-28): REABRE una cancelacion <c>AbandonedByOperator</c> para registrar un
    /// REEMBOLSO TARDIO (el operador devolvio plata DESPUES de que el plazo venció y la cuenta se dio por perdida).
    /// La transicion es CONTROLADA y AUDITADA: la cancelacion vuelve a <c>AwaitingOperatorRefund</c> con un plazo
    /// nuevo (<c>OperatorRefundDueBy</c> = ahora + <c>OperatorRefundTimeoutDays</c>) para que el job de timeout no
    /// la vuelva a abandonar de inmediato; <c>ClosedAt</c> se limpia. Una vez reabierta, el cashier registra el
    /// ingreso (<c>OperatorRefundService.RecordReceivedAsync</c>) y lo imputa (<c>AllocateAsync</c>) con el circuito
    /// NORMAL, que genera el saldo a favor del CLIENTE de la reserva.
    ///
    /// <para><b>La RESERVA NO se resucita</b>: el viaje sigue cancelado (<c>Cancelled</c>). Reabrir es solo para
    /// el circuito de plata del operador; el reembolso tardio se vuelve saldo a favor del cliente al imputarse.</para>
    ///
    /// <para><b>FIX A (2026-07-04)</b>: ademas de <c>AbandonedByOperator</c>, tambien reabre una cancelacion
    /// <c>Closed</c> CON RESIDUO real del operador (reembolso parcial: el operador devolvio de menos, el cliente
    /// consumio su saldo y la BC se cerro, pero el operador todavia debe plata — receivable vivo &gt; 0 con la
    /// MISMA formula del extracto). Sin residuo, una <c>Closed</c> sigue rechazando. El neto del reembolso tardio
    /// sigue el circuito NORMAL de allocation; al consumirse el saldo del cliente la BC vuelve a <c>Closed</c>.</para>
    ///
    /// <para><b>Idempotencia</b>: si la cancelacion ya esta en <c>AwaitingOperatorRefund</c> o
    /// <c>ClientCreditApplied</c> (ya esta abierta / ya recibio algo), es no-op y devuelve el DTO actual sin
    /// re-auditar. Si esta en cualquier otro estado que no sea <c>AbandonedByOperator</c> ni <c>Closed</c>-con-residuo
    /// (Drafted, Aborted, AwaitingFiscalConfirmation, etc.), rechaza con <c>BusinessInvariantViolationException</c>.
    /// La auditoria distingue el origen por <c>previousStatus</c> en el detalle del evento.</para>
    ///
    /// <para><b>Permiso</b>: lo gatea el controller con <c>caja.edit</c> (mismo permiso que registrar el reembolso).
    /// El motivo (&gt;= 10 chars) es obligatorio para la auditoria. Marca durable del "tardio" = el audit log
    /// <c>BookingCancellationReopenedForLateRefund</c> (no se agrega columna; decision documentada).</para>
    /// </summary>
    Task<BookingCancellationDto> ReopenAbandonedForLateRefundAsync(
        Guid publicId,
        string reason,
        string userId,
        string? userName,
        CancellationToken ct);

    // ===== FC1.3.3 — comando publico para NC parcial (admin edita liquidacion en manual review) =====

    /// <summary>
    /// FC1.3.3 (ADR-009 §2.7 G3, 2026-05-21): el admin edita los inputs de la
    /// liquidacion fiscal de un BC que esta en
    /// <c>BookingCancellationStatus.ManualReviewPending</c>. Reglas resumidas:
    /// <list type="bullet">
    ///   <item>BC debe estar en <c>ManualReviewPending</c> (sino rechaza).</item>
    ///   <item>4-eyes (INV-FC1.3-004): admin != vendedor original, salvo bypass
    ///         GR-005 (single admin) con comentario reforzado 100+ chars.</item>
    ///   <item>El calculator vuelve a correr con los overrides y se persiste
    ///         el resultado en el <c>ApprovalRequest.Metadata.edits[]</c>.</item>
    ///   <item>El BC se queda en <c>ManualReviewPending</c> (self-loop). El
    ///         approve/reject se hace por el endpoint generico de approvals,
    ///         no por este metodo.</item>
    ///   <item>Audit obligatorio (<c>BookingCancellationLiquidationEdited</c>)
    ///         con diff JSON {"Field":{"Old":"...","New":"..."}}.</item>
    /// </list>
    ///
    /// <para><b>Approve/Reject NO se exponen como metodos publicos del service</b>:
    /// el flujo canonico es invocar <c>ApprovalRequestService.ApproveAsync</c> /
    /// <c>RejectAsync</c> del controller generico de approvals, que despues
    /// dispara los callbacks del bridge <c>IPartialCreditNoteApprovalBridge</c>.
    /// Asi se evita duplicar la maquina de estados del approval en dos lados.</para>
    /// </summary>
    Task<BookingCancellationDto> EditLiquidationAsync(
        Guid publicId,
        EditLiquidationRequest req,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// ADR-014 (§3.1/§3.2, 2026-06-02): confirmacion DIFERIDA de la penalidad propia de
    /// la agencia, DIAS DESPUES de la cancelacion. Valida las precondiciones (estado
    /// post-NC con CAE, flag, permiso, concepto agency-owned, idempotencia, fecha),
    /// persiste <c>PenaltyStatus=Confirmed</c> + monto + fecha del operador + auditoria en
    /// un COMMIT PROPIO (la marca de no-retorno del exactly-once, §3.4), y recien entonces
    /// dispara la ND reusando el motor de ADR-013. La ND es SOLO comprobante fiscal: NO
    /// toca el balance ni reabre la reserva (B2, §3.4-bis).
    ///
    /// <para><b>Exceptions</b>: <c>KeyNotFoundException</c> (404) si el BC no existe;
    /// <c>InvalidOperationException</c> (409) si el flag esta OFF o el estado/concepto no
    /// procede (codigos INV-ADR014-*); <c>BusinessInvariantViolationException</c> (409
    /// idempotente INV-ADR014-003 / permiso); <c>ApprovalRequiredException</c> (409
    /// requiresApproval) si el caso exige 4-eyes y no hay approval valido;
    /// <c>ArgumentException</c> (400) si la fecha es invalida;
    /// <c>DbUpdateConcurrencyException</c> (409 CONCURRENT_EDIT) por xmin.</para>
    /// </summary>
    /// <param name="userCanClassifyAgencyPenalty">
    /// ADR-014: true si el caller tiene <c>cancellations.classify_agency_penalty</c> (o es
    /// Admin). Lo resuelve el controller. El service lo EXIGE (no degrada): confirmar una
    /// penalidad propia diferida es disparar una ND fiscal real.
    /// </param>
    Task<BookingCancellationDto> ConfirmPenaltyAsync(
        Guid publicId,
        ConfirmPenaltyRequest request,
        string userId,
        string? userName,
        bool requesterIsAdmin,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false);

    /// <summary>
    /// RECUPERACION (fix 2026-07-01): reintenta EMITIR la Nota de Debito de una cancelacion cuya multa YA quedo
    /// confirmada (<c>PenaltyStatus=Confirmed</c>) pero cuya ND nunca se llego a emitir/vincular
    /// (<c>DebitNoteInvoiceId=null</c>) porque un intento anterior fallo (emision o reconciler). Es el camino para
    /// DESTRABAR una cancelacion que quedo a medias: <see cref="ConfirmPenaltyAsync"/> rebota por idempotencia y
    /// cerrar sin multa tambien, asi que sin este comando la reserva queda visible en la bandeja de NDs por
    /// revisar pero sin ninguna accion posible. NO re-confirma la multa: solo re-dispara la ND.
    ///
    /// <para><b>Anti doble-emision</b>: si ya existe una ND para la factura original de la cancelacion (creada en
    /// un intento previo pero no vinculada), la RE-VINCULA en vez de emitir otra. Solo si no hay ninguna, emite de
    /// cero, con el MISMO blindaje que confirm-penalty: si la emision vuelve a fallar, deja la ND en revision
    /// manual y devuelve EXITO-con-aviso (nunca un 500).</para>
    ///
    /// <para><b>Precondiciones</b> (de ESTADO, sin re-confirmar la multa): flag ON, BC existe, multa ya Confirmed,
    /// ND aun no vinculada, estado post-NC con CAE. <b>Permiso</b>: mismo gate fiscal que confirm-penalty
    /// (<c>cancellations.classify_agency_penalty</c> o Admin), resuelto por el controller y EXIGIDO por el service.</para>
    ///
    /// <para><b>Exceptions</b>: <c>KeyNotFoundException</c> (404); <c>InvalidOperationException</c> (409, flag OFF);
    /// <c>BusinessInvariantViolationException</c> (409: INV-ADR014-RETRY-PERM sin permiso; INV-ADR014-RETRY-001
    /// multa no confirmada; INV-ADR014-RETRY-002 ND ya en juego; INV-ADR014-RETRY-003 NC sin CAE);
    /// <c>DbUpdateConcurrencyException</c> (409 CONCURRENT_EDIT).</para>
    /// </summary>
    Task<BookingCancellationDto> RetryDebitNoteEmissionAsync(
        Guid publicId,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false);

    /// <summary>
    /// Spec "el paso de multa vive en la ficha" (A4, 2026-07-08): corrige el MONTO + MONEDA de una multa YA
    /// CONFIRMADA cuya Nota de Debito quedo TRABADA (revision manual por moneda distinta, o fallida) y SIN
    /// comprobante fiscal emitido con CAE. Es la version ATOMICA del circuito que hoy existe pieza por pieza
    /// (cerrar sin multa -> reabrir -> volver a confirmar con la moneda nueva): deshace la imputacion vieja, graba
    /// el monto/moneda nuevos, y re-encola la ND, TODO bajo el MISMO lock FOR UPDATE del padre.
    ///
    /// <para><b>Guard duro</b>: si ya hay una ND emitida con CAE (o encolada en vuelo), NO se corrige por este
    /// camino (409 INV-CORRECT-002/003): habria que anular ese comprobante desde administracion primero. El
    /// re-check se hace tambien DENTRO del lock FOR UPDATE (anti carrera con un retry concurrente).</para>
    ///
    /// <para><b>Permiso</b>: MISMO gate que confirm-penalty/retry (<c>cancellations.classify_agency_penalty</c> o
    /// Admin), resuelto server-side por el controller y EXIGIDO por el service (INV-CORRECT-PERM).</para>
    ///
    /// <para><b>Exceptions</b>: <c>ArgumentException</c> (400: monto &lt;= 0, moneda no ISO ARS/USD, motivo vacio);
    /// <c>KeyNotFoundException</c> (404); <c>InvalidOperationException</c> (409, flag OFF);
    /// <c>BusinessInvariantViolationException</c> (409: INV-CORRECT-PERM sin permiso; INV-CORRECT-001 multa no
    /// confirmada; INV-CORRECT-002 ND ya emitida con CAE; INV-CORRECT-003 ND en vuelo);
    /// <c>DbUpdateConcurrencyException</c> (409 CONCURRENT_EDIT).</para>
    /// </summary>
    /// <param name="amount">Monto corregido de la multa. Obligatorio &gt; 0.</param>
    /// <param name="currency">Moneda ISO 4217 corregida (ARS/USD).</param>
    /// <param name="reason">Motivo OBLIGATORIO de la correccion (auditoria del contador).</param>
    /// <param name="userCanClassifyAgencyPenalty">true si el caller puede resolver la pata fiscal (permiso o Admin).</param>
    /// <param name="exchangeRate">
    /// ADR-044 Fix B (2026-07-13): tipo de cambio (ARS por 1 USD) para convertir una multa declarada en una
    /// moneda distinta a la de la factura (Caso A). Requerido cuando hay que convertir; el service revalida.
    /// </param>
    /// <param name="exchangeRateSource">Origen del TC (ver <c>ExchangeRateSource</c>). Manual/sin especificar exige justificacion.</param>
    /// <param name="exchangeRateDate">Fecha del TC (dia en que el operador cobro). Requerida cuando hay que convertir.</param>
    /// <param name="exchangeRateJustification">Justificacion del TC. Obligatoria cuando el origen es Manual (INV-120).</param>
    Task<BookingCancellationDto> CorrectPenaltyAsync(
        Guid publicId,
        decimal amount,
        string currency,
        string reason,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false,
        decimal? exchangeRate = null,
        int? exchangeRateSource = null,
        DateTime? exchangeRateDate = null,
        string? exchangeRateJustification = null);

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): la Nota de Debito de la multa salio con CAE y
    /// estaba MAL (monto/moneda equivocada, o no correspondia). Emite una Nota de Credito ESPEJO de esa ND
    /// (<c>OriginalInvoiceId = la ND</c>, nunca la factura original) que la anula fiscalmente. Al conseguir CAE
    /// (async, reconciliado por <c>DebitNoteAnnulmentReconciliation</c>), la ND queda desvinculada del BC y el
    /// paso de la multa vuelve a estar ABIERTO (<c>ConfirmedNoDebitNote</c>: corregir y re-emitir, o cerrar sin
    /// multa). Si la multa estaba cobrada (total o parcialmente), la porcion cobrada se convierte en saldo a
    /// favor del cliente. El ciclo emitir→deshacer→re-emitir se puede repetir (molde de <see cref="CorrectPenaltyAsync"/>).
    ///
    /// <para><b>Permiso</b>: SOLO ADMINISTRADORES (spec UX firmada, gate 2026-07-14). A diferencia de
    /// confirm-penalty/correct-penalty/retry (permiso <c>cancellations.classify_agency_penalty</c>), deshacer un
    /// comprobante fiscal ya emitido con CAE se restringe al rol Admin. Resuelto server-side por el controller y
    /// EXIGIDO por el service (INV-UNDO-PERM).</para>
    ///
    /// <para><b>Exceptions</b>: <c>ArgumentException</c> (400: motivo vacio); <c>KeyNotFoundException</c> (404);
    /// <c>InvalidOperationException</c> (409, flag OFF); <c>BusinessInvariantViolationException</c> (409:
    /// INV-UNDO-PERM sin ser Admin; INV-UNDO-001 sin ND con CAE para deshacer; INV-UNDO-002 ya hay una anulacion
    /// en curso o consumada; INV-UNDO-MANUAL factura original ya anulada del todo o ND con tributos -> revision
    /// manual; INV-UNDO-MULTIOP ambiguedad irresoluble entre operadores); <c>DbUpdateConcurrencyException</c>
    /// (409 CONCURRENT_EDIT).</para>
    /// </summary>
    /// <param name="reason">Motivo OBLIGATORIO de por que la ND estaba mal (auditoria del contador).</param>
    /// <param name="requesterIsAdmin">true si el caller es Admin. Lo resuelve el controller; el service lo EXIGE.</param>
    Task<BookingCancellationDto> UndoIssuedDebitNoteAsync(
        Guid publicId,
        string reason,
        string userId,
        string? userName,
        CancellationToken ct,
        bool requesterIsAdmin = false);

    /// <summary>
    /// ADR-042 §3.6 (2026-07-01): reintenta SOLO las notas de credito faltantes de una anulacion multi-factura
    /// que quedo a medias (una NC salio y otra fallo, o quedo atascada). Idempotente: NO re-emite las NC que
    /// ya salieron (anti doble-emision: re-vincula una NC huerfana ya creada). Serializado por el mismo lock
    /// pesimista del padre que los callbacks de ARCA (un segundo retry concurrente ve la hija ya Pending -> no-op).
    ///
    /// <para><b>Permiso</b>: MISMO que anular la reserva (<c>ReservasCancel</c>), resuelto server-side por el
    /// controller (defensa en profundidad). No es "deshacer", es COMPLETAR lo ya autorizado al confirmar; por
    /// eso cualquier vendedor que podia anular puede reintentar (no se restringe a Admin).</para>
    ///
    /// <para><b>Exceptions</b>: <c>KeyNotFoundException</c> (404); <c>BusinessInvariantViolationException</c>
    /// (409 INV-042-RETRY-001 si el estado no es reintentable); nunca un 500 que trabe la reserva.</para>
    /// </summary>
    Task<BookingCancellationDto> RetryCreditNotesAsync(
        Guid publicId,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// Fase A (2026-06-28): cierra la pata de la penalidad del operador SIN multa ("el operador no cobro multa /
    /// devuelve todo"). Es la rama ALTERNATIVA a <see cref="ConfirmPenaltyAsync"/>: comparten el mismo candado
    /// (la primera que resuelve la penalidad gana). Deja la penalidad en el estado terminal
    /// <c>PenaltyStatus.Waived</c> con monto 0, asi <c>HasPendingOperatorPenalty</c> pasa a false y el boton
    /// pendiente se limpia, SIN emitir ninguna Nota de Debito ni inventar una penalidad.
    ///
    /// <para><b>Que NO hace</b>: no emite ND ni ningun comprobante fiscal (la NC total al cliente ya salio al
    /// anular); no toca <c>RefundCap</c>/<c>PenaltyAmount</c> de las lineas (el operador devuelve TODO, los caps
    /// quedan completos), por lo que el cierre por reembolso total
    /// (<c>CloseReservaIfOperatorRefundComplete</c>) sigue funcionando; no cambia por si mismo el estado de la
    /// reserva (cierra cuando llega el reembolso completo).</para>
    ///
    /// <para><b>Auditoria OBLIGATORIA</b>: deja el evento <c>AuditActions.OperatorPenaltyWaived</c> con
    /// quien/cuando + el motivo (para distinguir "no hubo multa" de "monto 0 por error").</para>
    ///
    /// <para><b>Cierre sin multa DESDE una multa ya confirmada</b> (fix "multa fantasma", 2026-07-05): si la multa
    /// se confirmo pero su Nota de Debito nunca llego a existir (quedo <c>NotApplicable</c>, <c>Failed</c> o
    /// <c>ManualReview</c>, y sin factura de ND vinculada), el usuario puede decidir NO cobrarla. Esa rama
    /// exige rol Admin (misma sensibilidad que reabrir un cierre) y, ademas de dejar la penalidad en
    /// <c>Waived</c>, RESTAURA los topes de reembolso del operador que la confirmacion habia reducido (espejo de
    /// la imputacion de la multa a las lineas). Si en cambio ya hay un comprobante fiscal en juego (ND vinculada,
    /// o en estado <c>Pending</c>/<c>Issued</c>), NO se puede cerrar sin multa por este camino: se resuelve desde
    /// administracion (INV-WAIVE-004).</para>
    ///
    /// <para><b>Exceptions</b>: <c>KeyNotFoundException</c> (404) si el BC no existe;
    /// <c>InvalidOperationException</c> (409) si el flag esta OFF;
    /// <c>BusinessInvariantViolationException</c> (409) por falta de permiso (INV-WAIVE-PERM), estado no post-NC
    /// (INV-WAIVE-001), penalidad ya cerrada sin multa (INV-WAIVE-003, idempotencia: waive doble rebota),
    /// comprobante fiscal de la multa en juego (INV-WAIVE-004) o cierre desde multa confirmada sin ser Admin
    /// (INV-WAIVE-005); <c>DbUpdateConcurrencyException</c> (409 CONCURRENT_EDIT) por xmin.</para>
    /// </summary>
    /// <param name="reason">Motivo OBLIGATORIO del cierre sin multa (auditoria del contador).</param>
    /// <param name="userCanClassifyAgencyPenalty">true si el caller tiene
    /// <c>cancellations.classify_agency_penalty</c> (o es Admin). Lo resuelve el controller. El service lo EXIGE,
    /// igual que <see cref="ConfirmPenaltyAsync"/>: resolver la pata fiscal de la penalidad es una accion sensible.</param>
    /// <param name="requesterIsAdmin">true si el caller es Admin. Solo se EXIGE para cerrar sin multa DESDE una
    /// multa ya confirmada (rama sensible); el cierre desde el estado pendiente normal no lo requiere. Lo resuelve
    /// el controller.</param>
    /// <param name="supplierPublicId">
    /// ADR-044 T1 (2026-07-10): identificador PUBLICO del operador cuya pata de multa se cierra sin multa, para
    /// cancelaciones con servicios de MAS de un operador (ADR-025). Opcional y retrocompatible: si la cancelacion
    /// tiene lineas de UN solo operador (el 100% de los casos hoy), se resuelve solo.
    /// </param>
    Task<BookingCancellationDto> WaiveOperatorPenaltyAsync(
        Guid publicId,
        string reason,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false,
        bool requesterIsAdmin = false,
        Guid? supplierPublicId = null);

    /// <summary>
    /// Fase A (2026-06-28): REABRE un cierre sin multa, volviendo la penalidad del operador de
    /// <c>PenaltyStatus.Waived</c> a <c>Estimated</c> (su estado pendiente). Existe porque el cierre sin multa es
    /// terminal y, sin esta reversa, un error de carga o una multa TARDIA del operador no se podria corregir desde
    /// el sistema. Tras revertir, <c>HasPendingOperatorPenalty</c> vuelve a dar true y quedan disponibles otra vez
    /// tanto "Confirmar multa" como volver a cerrar sin multa.
    ///
    /// <para><b>Solo Admin</b>: es una accion sensible y poco habitual. El controller rechaza con 403 a quien no es
    /// Admin; el service lo EXIGE igual (defensa en profundidad, INV-WAIVE-REVERT-PERM).</para>
    ///
    /// <para><b>Reversa limpia</b>: como el waive NO emitio Nota de Debito ni toco las lineas
    /// (<c>RefundCap</c>/<c>PenaltyAmount</c> intactos, <c>DebitNoteStatus=NotApplicable</c>, sin Invoice), revertir
    /// es solo restaurar los defaults del estado <c>Estimated</c> (monto, confirmado-por y fecha a null). Si por
    /// algun motivo el BC tuviera una ND vinculada (no deberia para un waive), se RECHAZA (INV-WAIVE-REVERT-002).</para>
    ///
    /// <para><b>Auditoria OBLIGATORIA</b>: deja el evento <c>AuditActions.OperatorPenaltyWaiveReverted</c> con
    /// quien/cuando + el motivo, atomico con el cambio de estado (mismo SaveChanges).</para>
    ///
    /// <para><b>Exceptions</b>: <c>KeyNotFoundException</c> (404) si el BC no existe;
    /// <c>InvalidOperationException</c> (409) si el flag esta OFF;
    /// <c>BusinessInvariantViolationException</c> (409) por no ser Admin (INV-WAIVE-REVERT-PERM), no estar cerrada sin
    /// multa (INV-WAIVE-REVERT-001, idempotencia) o tener una ND en juego (INV-WAIVE-REVERT-002);
    /// <c>ArgumentException</c> (400) si el motivo es vacio;
    /// <c>DbUpdateConcurrencyException</c> (409 CONCURRENT_EDIT) por xmin.</para>
    /// </summary>
    /// <param name="reason">Motivo OBLIGATORIO de la reapertura (auditoria del contador).</param>
    /// <param name="requesterIsAdmin">true si el caller es Admin. Lo resuelve el controller. El service lo EXIGE.</param>
    /// <param name="supplierPublicId">
    /// ADR-044 T1 (2026-07-10): identificador PUBLICO del operador cuya pata de multa se reabre, para cancelaciones
    /// con servicios de MAS de un operador (ADR-025). Opcional y retrocompatible: si la cancelacion tiene lineas de
    /// UN solo operador (el 100% de los casos hoy), se resuelve solo. Sin este parametro un secundario cerrado sin
    /// multa quedaria irreversible (el snapshot del padre describe al principal).
    /// </param>
    Task<BookingCancellationDto> RevertWaivedOperatorPenaltyAsync(
        Guid publicId,
        string reason,
        string userId,
        string? userName,
        bool requesterIsAdmin,
        CancellationToken ct,
        Guid? supplierPublicId = null);

    /// <summary>
    /// ADR-044 T2 Addendum (2026-07-10): agrega un cargo SECUNDARIO del operador sobre una multa YA confirmada
    /// (ej. una retencion fiscal ademas del cargo administrativo que el confirm ya creo por detras). Accion
    /// OPCIONAL: el flujo simple (confirmar multa) no la necesita ni la muestra por default.
    ///
    /// <para><b>Precondiciones</b>: flag ON; BC existe; permiso <c>cancellations.classify_agency_penalty</c> (o
    /// Admin); el operador resuelto ya tiene su multa <c>Confirmed</c> (no se puede agregar un cargo secundario
    /// antes del cargo base); el operador NO es <see cref="Domain.Entities.SupplierInvoicingMode.CommissionOnly"/>
    /// (gate Decision A del Addendum); la moneda del cargo coincide con la de al menos una linea del operador
    /// (B2); <c>DocumentRef</c> obligatorio si <c>CollectionMode = FacturadaAparte</c>.</para>
    ///
    /// <para><b>Efecto en la plata</b>: si <c>CollectionMode = Retenida</c> y <c>Kind != Withholding</c>, reduce
    /// el <c>RefundCap</c> restante del operador (mismo reparto proporcional que el confirm automatico) y suma a
    /// <c>RetainedDeductionAmount</c>. Si <c>Kind == Withholding</c>, NUNCA reduce el <c>RefundCap</c> ni el
    /// credito del cliente (credito fiscal de la agencia). Si <c>CollectionMode = FacturadaAparte</c>, NUNCA
    /// reduce el <c>RefundCap</c> (el operador devuelve integro; el cargo es una deuda nueva hacia el operador).
    /// Todo cargo con <c>Kind != Withholding</c> suma a <c>PenaltyAmount</c> (eje CLIENTE) sin importar la forma
    /// de cobro. Ver <c>BookingCancellationLineOperatorCharge</c>.</para>
    ///
    /// <para><b>Exceptions</b>: <c>KeyNotFoundException</c> (404); <c>InvalidOperationException</c> (409, flag
    /// OFF); <c>BusinessInvariantViolationException</c> (409: INV-ADR044-CHARGE-PERM sin permiso;
    /// INV-ADR044-CHARGE-001 multa aun no confirmada; INV-ADR044-T2-COMMISSIONONLY operador intermediario);
    /// <c>ArgumentException</c> (400: moneda no coincide con ninguna linea del operador, o
    /// <c>DocumentRef</c> vacio con <c>FacturadaAparte</c>); <c>DbUpdateConcurrencyException</c> (409
    /// CONCURRENT_EDIT).</para>
    /// </summary>
    Task<BookingCancellationDto> AddOperatorChargeAsync(
        Guid publicId,
        AddOperatorChargeRequest request,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false);

    /// <summary>
    /// ADR-044 T3b Decision 1 (2026-07-10): fija o corrige la factura de venta destino
    /// (<see cref="Domain.Entities.BookingCancellationLineOperatorCharge.TargetInvoiceId"/>) de UN cargo puntual
    /// del operador, para cuando la reserva tiene 2+ facturas de venta activas (ADR-042) y el motor de emision
    /// de la Nota de Debito no puede autocompletarla sola. La pantalla que llama esto (desplegable de facturas
    /// activas, oculta si hay 1 sola) es ADR-044 T4.
    ///
    /// <para><b>Precondiciones</b>: flag ON; BC y cargo existen; permiso <c>cancellations.classify_agency_penalty</c>
    /// (o Admin); la factura elegida es miembro de las facturas de venta ACTIVAS de la reserva (viva, con CAE, no
    /// NC/ND); M2 — si la <see cref="Domain.Entities.BookingCancellationLineOperatorCharge.Kind"/> no es
    /// <c>Withholding</c>, ningun OTRO cargo trasladable (<c>Kind != Withholding</c>) de la MISMA linea puede
    /// tener ya una factura destino distinta (rechaza el conflicto; los <c>Withholding</c> quedan exentos porque
    /// nunca emiten renglon de ND).</para>
    ///
    /// <para><b>Exceptions</b>: <c>KeyNotFoundException</c> (404, BC/cargo/factura no existen);
    /// <c>InvalidOperationException</c> (409, flag OFF); <c>BusinessInvariantViolationException</c> (409:
    /// INV-ADR044-CHARGE-PERM sin permiso; INV-ADR044-TARGETINVOICE-001 factura no es miembro de las activas de
    /// la reserva; INV-ADR044-TARGETINVOICE-002 conflicto M2 con otro cargo de la misma linea;
    /// INV-ADR044-TARGETINVOICE-003 la ND al cliente ya se emitio / esta en vuelo, la factura ya no se cambia).</para>
    /// </summary>
    Task<BookingCancellationDto> SetOperatorChargeTargetInvoiceAsync(
        Guid publicId,
        Guid chargePublicId,
        SetOperatorChargeTargetInvoiceRequest request,
        string userId,
        string? userName,
        CancellationToken ct,
        bool userCanClassifyAgencyPenalty = false);

    // ===== Queries =====

    /// <summary>Obtiene un BC por su PublicId. Null si no existe.</summary>
    Task<BookingCancellationDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct);

    /// <summary>
    /// ADR-014 (read-model, 2026-06-23): obtiene la cancelacion VIGENTE de una reserva por el
    /// PublicId de la reserva. "Vigente" = la mas reciente que NO fue abortada (INV-081 garantiza
    /// una sola cancelacion activa por reserva, asi que en la practica es una sola). Null si la
    /// reserva no tiene ninguna cancelacion no-abortada.
    ///
    /// <para>Existe para el panel "Confirmar multa del operador": antes el frontend buscaba la
    /// cancelacion en la bandeja back-office de NDs pendientes, que filtra por estado de la ND y
    /// dejaba afuera el caso pass-through (penalidad estimada, ND aun no aplicable). Con este
    /// endpoint el frontend va directo a la cancelacion de la reserva y usa
    /// <c>CanConfirmPenalty</c>/<c>ConfirmPenaltyBlockedReason</c> del DTO para decidir.</para>
    /// </summary>
    Task<BookingCancellationDto?> GetByReservaAsync(Guid reservaPublicId, CancellationToken ct);

    /// <summary>
    /// H3 (2026-06-24): ¿la reserva tiene una MULTA DEL OPERADOR pendiente de confirmar ahora mismo (la
    /// confirmacion diferida que emite la Nota de Debito pass-through)? true SOLO si su cancelacion vigente cumple
    /// las precondiciones de ESTADO de <c>ConfirmPenaltyAsync</c> (flag maestro ON, NC total con CAE, penalidad aun
    /// Estimated, sin ND en juego). NO mira permiso ni 4-eyes.
    ///
    /// <para>Lo consume el armado del DETALLE de la reserva (<c>ReservaService</c>) para alimentar la capacidad
    /// <c>CanConfirmOperatorPenalty</c>: asi el front muestra "Confirmar multa del operador" SOLO cuando realmente
    /// hay algo que confirmar, sin adivinar por estado. Comparte la misma derivacion canonica que el read-model
    /// <c>CanConfirmPenalty</c> del DTO de la cancelacion (fuente unica, no divergen). El endpoint confirm-penalty
    /// revalida todo server-side, asi que esto es una pista de UI. false si la reserva no tiene cancelacion.</para>
    /// </summary>
    Task<bool> HasPendingOperatorPenaltyAsync(Guid reservaPublicId, CancellationToken ct);

    /// <summary>
    /// Fase A (2026-06-28): RESULTADO de la pata "multa del operador" de la cancelacion VIGENTE de la reserva:
    /// None / Pending / Confirmed / Waived. Es la version completa de <see cref="HasPendingOperatorPenaltyAsync"/>
    /// (que solo dice si es Pending): ademas distingue el cierre SIN multa (Waived) y la multa ya confirmada
    /// (Confirmed), que el front necesita para mostrar el cartel correcto al cargar la ficha de la reserva.
    ///
    /// <para>Pending reusa la MISMA derivacion canonica que <see cref="HasPendingOperatorPenaltyAsync"/> /
    /// el read-model <c>CanConfirmPenalty</c> (fuente unica, no divergen). Waived/Confirmed se leen directo del
    /// estado persistido de la penalidad (la resolucion ya ocurrio, independiente del flag). None si la reserva
    /// no tiene cancelacion o su pata de operador todavia no esta en juego (ej: NC total sin CAE aun).</para>
    /// </summary>
    Task<OperatorPenaltyOutcome> GetOperatorPenaltyOutcomeAsync(Guid reservaPublicId, CancellationToken ct);

    /// <summary>
    /// Spec "el paso de multa vive en la ficha" (A2, 2026-07-08): read-model DETALLADO del PASO de la multa del
    /// operador de la cancelacion vigente, para que la ficha (ReservaDetailPage) muestre el cartel/boton exacto sin
    /// pedir aparte el detalle de la cancelacion. Mas fino que <see cref="GetOperatorPenaltyOutcomeAsync"/>: desglosa
    /// el "Confirmed" segun donde quedo la ND (encolada / fallida / trabada por moneda / emitida) y trae monto,
    /// moneda (ISO), timestamp y los botones habilitados segun estado + permiso.
    ///
    /// <para>Solo se usa en el DETALLE (no en el listado: seria N+1). State="None" si la reserva no tiene
    /// cancelacion vigente o su pata de operador no esta en juego. Los endpoints revalidan todo server-side.</para>
    /// </summary>
    /// <param name="userCanClassifyOperatorPenalty">
    /// true si el usuario puede resolver la pata fiscal de la penalidad (permiso <c>cancellations.classify_agency_penalty</c>
    /// o Admin), ya resuelto por el caller. Decide que botones se OFRECEN (canConfirm/canRetry/canCorrect).
    /// </param>
    /// <param name="isCallerAdmin">
    /// true si el caller tiene rol ADMIN. Se usa SOLO para <c>CanWaive</c> (cerrar sin multa una penalidad ya
    /// confirmada): esa accion exige Admin (INV-WAIVE-005), no basta el permiso classify. Sin este dato, un no-admin
    /// con el permiso veria el boton y al apretarlo rebotaria 409 (el anti-patron "boton que rebota").
    /// </param>
    Task<OperatorPenaltySituationDto> GetOperatorPenaltySituationAsync(
        Guid reservaPublicId, bool userCanClassifyOperatorPenalty, bool isCallerAdmin, CancellationToken ct);

    /// <summary>
    /// ADR-044 T1 (2026-07-10): version LISTA de <see cref="GetOperatorPenaltySituationAsync"/>, un elemento POR
    /// OPERADOR con multa en juego en la cancelacion vigente de la reserva (una cancelacion puede tener servicios
    /// de mas de un operador, ADR-025 — hoy el 100% de las reservas activas tienen UNO solo, asi que en la
    /// practica la lista trae UN elemento con el MISMO contenido que el singular).
    ///
    /// <para>El PRIMER elemento (si la lista no esta vacia) es SIEMPRE el resultado exacto de
    /// <see cref="GetOperatorPenaltySituationAsync"/> para el operador principal — garantia de paridad byte a
    /// byte para el caso mono-operador. Los elementos siguientes (si los hay) son operadores SECUNDARIOS,
    /// derivados de <see cref="Domain.Entities.BookingCancellationLine"/> — la unica fuente que sabe la multa
    /// individual de cada uno. Lista VACIA = nada que mostrar (equivalente al singular con <c>State="None"</c>).
    /// </para>
    /// </summary>
    /// <param name="canSeeCost">
    /// ADR-044 T2 Addendum (security, 2026-07-10): true si el caller tiene visibilidad de COSTO
    /// (<c>cobranzas.see_cost</c> o Admin). El desglose de cargos del operador (<c>OperatorChargeDto.Amount</c>)
    /// es dato de costo — mismo criterio que enmascara <c>PenaltyRetained</c>/<c>PaidToOperator</c> en
    /// <c>OperatorRefundReadModelService</c>. Sin visibilidad de costo, la lista <c>Charges</c> viaja VACIA. El
    /// caller (<c>ReservaService</c>) resuelve el permiso; default true para no romper tests/otros callers que ya
    /// operan con contexto de costo.
    /// </param>
    Task<IReadOnlyList<OperatorPenaltySituationDto>> GetOperatorPenaltySituationsAsync(
        Guid reservaPublicId, bool userCanClassifyOperatorPenalty, bool isCallerAdmin, CancellationToken ct,
        bool canSeeCost = true);

    /// <summary>
    /// ADR-013 §3.10 (M4, 2026-06-01): bandeja "cancelaciones con NC emitida pero sin su
    /// ND". Devuelve los BCs cuya NC total ya salio (CreditNoteInvoiceId seteado) pero cuya
    /// ND quedo en <c>Pending</c> o <c>Failed</c> -> fiscalmente incompletas.
    ///
    /// <para>Como efecto secundario RECONCILIA el estado de la ND: para los que estan en
    /// <c>Pending</c>, lee el <c>Resultado</c> de la Invoice ND vinculada (que la emite el
    /// job async) y, si ya tiene CAE (Aprobado) o fue Rechazada, transiciona el
    /// <c>DebitNoteStatus</c> a <c>Issued</c>/<c>Failed</c>. Asi la bandeja se va limpiando
    /// sola a medida que ARCA responde, sin necesitar un callback dedicado.</para>
    /// </summary>
    Task<IReadOnlyList<CancellationDebitNotePendingDto>> GetCancellationsWithMissingDebitNoteAsync(
        CancellationToken ct);

    /// <summary>
    /// ADR-009/ADR-025 (read-model, 2026-06-13): bandeja "Notas de credito por revisar". Devuelve los
    /// BCs que estan esperando revision/emision manual de la NC parcial, es decir en estado
    /// <c>BookingCancellationStatus.ManualReviewPending</c> (o <c>RequiresManualReview</c>, que bajo el
    /// flujo normal no se persiste pero se incluye por completitud).
    ///
    /// <para>Es SOLO LECTURA (no reconcilia ni muta nada, a diferencia de
    /// <see cref="GetCancellationsWithMissingDebitNoteAsync"/>). El front la usa para listar las
    /// cancelaciones pendientes de aprobar/emitir su NC y navegar a cada una por PublicId.</para>
    /// </summary>
    Task<IReadOnlyList<PendingCreditNoteReviewDto>> GetCancellationsPendingCreditNoteReviewAsync(
        CancellationToken ct);
}
