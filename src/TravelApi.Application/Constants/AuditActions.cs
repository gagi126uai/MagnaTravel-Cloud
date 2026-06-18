namespace TravelApi.Application.Constants;

/// <summary>
/// FC1.2.1 v3 (2026-05-17): catalogo de strings de <c>action</c> que se pasan
/// a <c>IAuditService.LogBusinessEventAsync(action, ...)</c>.
///
/// <para>
/// <b>Por que constants en vez de string literals</b>: si un dia renombrabamos
/// "BookingCancellationConfirmed" en el service pero olvidabamos hacerlo en
/// los tests / dashboards, los reports de auditoria quedaban desincronizados
/// silenciosamente. Con constants compartidas, un rename rompe el build de
/// todos los callers — el cambio se detecta inmediato.
/// </para>
///
/// <para>
/// <b>Convencion de naming</b>: PascalCase, evento en pasado ("...Confirmed",
/// "...Aborted"). No agregar el prefijo "Audit" ni el sufijo "Event" — el caller
/// ya sabe que es un audit log. Si el evento tiene variantes manuales vs
/// automaticas (ej. Arca Confirmation), discriminar con sufijo claro
/// (..."Manually") para que las queries de auditoria puedan distinguirlas.
/// </para>
/// </summary>
public static class AuditActions
{
    // ===== Modulo cancelacion/refund (FC1.2) =====

    /// <summary>
    /// FC1.2.1: BC creado en estado <c>Drafted</c> (T-1). EntityName=BookingCancellation,
    /// EntityId = BC.Id (legacy int).
    /// </summary>
    public const string BookingCancellationDrafted = "BookingCancellationDrafted";

    /// <summary>
    /// FC1.2.1: BC pasa a <c>AwaitingFiscalConfirmation</c> (T0). El detalle JSON
    /// incluye <c>approvalRequestPublicId</c> cuando hubo override admin.
    /// </summary>
    public const string BookingCancellationConfirmed = "BookingCancellationConfirmed";

    /// <summary>
    /// FC1.2.1: BC abortado desde <c>Drafted</c> (sin side-effects fiscales).
    /// </summary>
    public const string BookingCancellationAborted = "BookingCancellationAborted";

    /// <summary>
    /// B1 (2026-06-03): un segundo <c>DraftAsync</c> sobre una reserva que ya tenia
    /// un draft "puro" (Drafted, sin NC ni ND) NO creo una fila nueva: reuso el draft
    /// existente (reintento idempotente del flujo draft -> confirm). Sirve para distinguir
    /// en auditoria cuantos drafts se reusaron vs cuantos se crearon de cero.
    /// </summary>
    public const string BookingCancellationDraftReused = "BookingCancellationDraftReused";

    /// <summary>
    /// B1 (2026-06-03): un BC que estaba en <c>ArcaRejected</c> (AFIP rechazo la NC,
    /// CAE no aprobado, sin nota de credito viva) se auto-abortio para permitir que el
    /// vendedor vuelva a cancelar la reserva por la via normal. El detalle JSON incluye
    /// el PublicId del BC liberado. Es seguro porque un ArcaRejected sin
    /// <c>CreditNoteInvoiceId</c> no dejo ningun comprobante fiscal vivo.
    /// </summary>
    public const string BookingCancellationAutoAbortedArcaRejected = "BookingCancellationAutoAbortedArcaRejected";

    /// <summary>
    /// FC1.2.1: AFIP devolvio CAE para la NC; el BC paso a
    /// <c>AwaitingOperatorRefund</c> automaticamente via callback Hangfire.
    /// </summary>
    public const string BookingCancellationArcaSucceeded = "BookingCancellationArcaSucceeded";

    /// <summary>
    /// FC1.2.1: AFIP rechazo la NC; el BC paso a <c>ArcaRejected</c>.
    /// El detalle JSON incluye el <c>afipErrorMessage</c>.
    /// </summary>
    public const string BookingCancellationArcaRejected = "BookingCancellationArcaRejected";

    /// <summary>
    /// FC1.2.1 v3 (BR-V2-01): Admin forzo la transicion fiscal usando el escape
    /// hatch <c>ForceArcaConfirmationAsync</c>. Discrimina del flujo automatico
    /// para queries de auditoria ("cuantas BCs se confirmaron por callback vs
    /// por boton manual?").
    /// </summary>
    public const string BookingCancellationArcaConfirmedManually = "BookingCancellationArcaConfirmedManually";

    /// <summary>
    /// FC1.2.1 v3 (BR-V2-01): variante no-op del Force cuando el BC ya
    /// transiciono via callback automatico antes de que el Admin apretara el
    /// boton. La operacion es idempotente (200 OK), pero el audit log distinto
    /// permite trazar el intento.
    /// </summary>
    public const string BookingCancellationArcaConfirmedManuallyNoOp = "BookingCancellationArcaConfirmedManually_NoOp";

    // ===== Modulo OperatorRefund (FC1.2.2) =====

    /// <summary>
    /// FC1.2.2 (2026-05-18): la agencia registra un ingreso fisico recibido del
    /// operador (transferencia, cheque, etc.) que se imputara N:M contra una o
    /// mas BCs via allocations. Triggea un <c>ManualCashMovement</c> Income.
    /// </summary>
    public const string OperatorRefundReceivedRegistered = "OperatorRefundReceivedRegistered";

    /// <summary>
    /// FC1.2.2 (2026-05-18): se asigna parte del refund recibido a un BC
    /// especifico (con sus deducciones tipificadas). En el detail JSON viaja el
    /// gross, el net, las deducciones y el ApprovalId (si aplico override).
    /// </summary>
    public const string OperatorRefundAllocated = "OperatorRefundAllocated";

    /// <summary>
    /// FC1.2.2 (2026-05-18): allocation anulada manualmente (cashier se equivoco,
    /// el BC esta mal imputado, etc.). Audit reforzado con motivo &gt;= 20 chars.
    /// Libera el cap del refund (decrementa <c>AllocatedAmount</c>).
    /// </summary>
    public const string OperatorRefundAllocationVoided = "OperatorRefundAllocationVoided";

    /// <summary>
    /// FC1.2.2 (2026-05-18): allocation movida atomicamente de un BC a otro
    /// (un solo audit en vez de void + allocate para que el reviewer entienda
    /// el evento unico). Metadata incluye old/newBcId, old/newAllocationId.
    /// </summary>
    public const string OperatorRefundAllocationReassociated = "OperatorRefundAllocationReassociated";

    // ===== Modulo ClientCredit (FC1.2.3) =====

    /// <summary>
    /// FC1.2.3 (2026-05-18): el cliente (o el cashier en su nombre) retiro
    /// saldo del <see cref="ClientCreditEntry"/>. Audit base para PhysicalCash /
    /// Transfer / KeptAsCredit / AppliedToNewBooking. Para ReversedToOperator
    /// existe un audit dedicado (ver <see cref="ClientRefundReversalApproved"/>)
    /// que se loguea ADEMAS de este (audit reforzado).
    /// </summary>
    public const string ClientCreditWithdrawn = "ClientCreditWithdrawn";

    /// <summary>
    /// FC1.2.3 (2026-05-18): variante del withdraw cuando la kind es
    /// <c>PhysicalCash</c> y el monto supera <c>PhysicalRefundAlertThreshold</c>
    /// (sin llegar a Ley 25.345). Es informativo: el dashboard del admin lo
    /// muestra como "Movimiento importante" sin bloquear la operacion.
    /// </summary>
    public const string ClientCreditPhysicalRefundAlert = "ClientCreditPhysicalRefundAlert";

    /// <summary>
    /// FC1.2.3 (2026-05-18): audit reforzado para el caso ReversedToOperator
    /// (el cliente devuelve plata ya retirada). Se logue ADEMAS del audit base
    /// <see cref="ClientCreditWithdrawn"/> para que el daily egress report pueda
    /// destacar el evento como "reversal post-egreso" (audit fiscal ADR-002 §8).
    /// </summary>
    public const string ClientRefundReversalApproved = "ClientRefundReversalApproved";

    /// <summary>
    /// FC4 (2026-06-14): el saldo a favor del cliente se APLICO como pago de OTRA reserva
    /// (kind <c>AppliedToNewBooking</c>). Se loguea ADEMAS del audit base
    /// <see cref="ClientCreditWithdrawn"/>, del LADO de la reserva destino: deja rastro de que esa reserva
    /// recibio plata desde un bolsillo del cliente (no fue un cobro de caja). Detalle: withdrawal, bolsillo,
    /// cliente, reserva destino, monto y moneda.
    /// </summary>
    public const string ClientCreditAppliedToBooking = "ClientCreditAppliedToBooking";

    /// <summary>
    /// FC1.2.3 (2026-05-18): cuando el ultimo withdraw deja el BC sin saldos
    /// pendientes (todos los entries en RemainingBalance=0), el BC pasa a
    /// <c>Closed</c> y la Reserva a <c>Cancelled</c>. El audit deja trazabilidad
    /// de quien gatillo el cierre y cuando.
    /// </summary>
    public const string BookingCancellationClosed = "BookingCancellationClosed";

    /// <summary>
    /// ADR-033 (2026-06-18): la RESERVA se cerro (<c>PendingOperatorRefund</c> -> <c>Cancelled</c>)
    /// porque el OPERADOR reembolso el total esperado (todas las lineas con <c>RefundCap</c> &gt; 0 quedaron
    /// <c>Settled</c>), SIN esperar a que el cliente consuma su saldo a favor. Es una via de cierre distinta
    /// a <see cref="BookingCancellationClosed"/>: aca el BC SIGUE en <c>ClientCreditApplied</c> (el cliente
    /// todavia tiene credito vivo en su bolsillo); lo unico que se cierra es la reserva. El credito del
    /// cliente NO se toca (ADR-033 desacoplo deuda/credito del estado de la reserva).
    /// </summary>
    public const string BookingCancellationClosedByOperatorRefund = "BookingCancellationClosedByOperatorRefund";

    // ===== Entity names (helpers) =====

    /// <summary>
    /// Nombre canonico de entidad que se pasa a <c>LogBusinessEventAsync(entityName: ...)</c>
    /// para todos los eventos del modulo. Si el frontend filtra audit logs por
    /// <c>entityName=BookingCancellation</c>, este es el valor a usar.
    /// </summary>
    public const string BookingCancellationEntityName = "BookingCancellation";

    /// <summary>FC1.2.2: entityName para eventos sobre el ingreso fisico del operador.</summary>
    public const string OperatorRefundReceivedEntityName = "OperatorRefundReceived";

    /// <summary>FC1.2.2: entityName para eventos sobre la allocation N:M.</summary>
    public const string OperatorRefundAllocationEntityName = "OperatorRefundAllocation";

    /// <summary>FC1.2.3: entityName para eventos sobre el retiro de saldo cliente.</summary>
    public const string ClientCreditWithdrawalEntityName = "ClientCreditWithdrawal";

    /// <summary>FC1.2.3: entityName para eventos sobre el saldo del cliente (entry).</summary>
    public const string ClientCreditEntryEntityName = "ClientCreditEntry";

    // ===== Modulo NC parcial Hotel (FC1.3.3) =====

    /// <summary>
    /// FC1.3.3 (ADR-009 §2.8.3, 2026-05-21): el clasificador identifico un caso
    /// que requiere revision manual y se abrio un <c>ApprovalRequest</c> tipo
    /// <c>PartialCreditNoteApproval</c>. El BC paso a <c>ManualReviewPending</c>.
    /// El JSON detail incluye <c>creditNoteKind</c>, <c>reviewRequiredReason</c>
    /// (bitflag string), <c>fiscalAmountToCredit</c>, <c>approvalRequestPublicId</c>.
    /// </summary>
    public const string BookingCancellationSubmittedForReview = "BookingCancellationSubmittedForReview";

    /// <summary>
    /// FC1.3.3 (ADR-009 §2.7 G3, 2026-05-21): el admin edito la liquidacion del
    /// BC mientras estaba en <c>ManualReviewPending</c>. El JSON detail incluye
    /// el diff <c>Changes</c> (RH-012, shape {"Field":{"Old":"...","New":"..."}}),
    /// el comentario del admin y el flag <c>selfApprovedDueToSingleAdmin</c>
    /// cuando GR-005 aplico.
    /// </summary>
    public const string BookingCancellationLiquidationEdited = "BookingCancellationLiquidationEdited";

    /// <summary>
    /// FC1.3.3 (ADR-009 §2.8.3, 2026-05-21): el admin aprobo la liquidacion del
    /// BC. El BC paso de <c>ManualReviewPending</c> a <c>ManualReviewApproved</c>.
    /// En Fase 1, inmediatamente despues avanza a <c>AwaitingFiscalConfirmation</c>
    /// y sigue el flujo FC1.2 (NC total real al ARCA). El JSON detail incluye el
    /// resolverNotes, el bypass GR-005 si aplico, y el flag de accounting review.
    /// </summary>
    public const string BookingCancellationManualReviewApproved = "BookingCancellationManualReviewApproved";

    /// <summary>
    /// FC1.3.3 (ADR-009 §2.8.3, 2026-05-21): el admin rechazo la liquidacion.
    /// El BC paso transitoriamente a <c>ManualReviewRejected</c> y en la misma
    /// transaccion se auto-reseteo a <c>Drafted</c> (limpia CreditNoteKind,
    /// ReviewRequiredReason, LiquidationComputed* y nulea la FK al approval).
    /// El detail JSON incluye el motivo del rechazo del admin y el snapshot
    /// pre-reset para auditoria.
    /// </summary>
    public const string BookingCancellationManualReviewRejected = "BookingCancellationManualReviewRejected";

    /// <summary>
    /// FC1.3.6b (ADR-009 §2.12 round 3, 2026-05-21): un admin uso el endpoint
    /// <c>POST /api/approvals/{publicId}/force-bridge-callback</c> para forzar
    /// la re-emision del callback del bridge sobre un approval que quedo
    /// desincronizado con su BC (job reconciliacion agoto los reintentos).
    /// El detail JSON incluye <c>ForcedBy</c>, <c>OverrideApprovalId</c>,
    /// <c>TargetApprovalId</c>, <c>TargetApprovalStatusAtForce</c>. Auditoria
    /// reforzada porque la accion bypassea el mecanismo automatico de
    /// reconciliacion y la decision queda 100% en manos del admin.
    /// </summary>
    public const string BookingCancellationForceApprovalCallback = "BookingCancellationForceApprovalCallback";

    /// <summary>
    /// FC1.3.6b (ADR-009 §2.12 round 3, 2026-05-21): el admin invoco
    /// force-callback pero el BC ya transiciono fuera de
    /// <c>ManualReviewPending</c> (ej. otro admin tambien forzo, o el job
    /// concilio justo antes). Es no-op idempotente: igualmente queda el
    /// rastro de auditoria para que se vea quien intento forzar y cuando.
    /// </summary>
    public const string BookingCancellationForceApprovalCallbackNoop = "BookingCancellationForceApprovalCallback_NoOp";

    // ===== ADR-031 v2.1 — Asignaciones pasajero <-> servicio =====

    /// <summary>
    /// ADR-031 v2.1 (2026-06-15): se asigno un pasajero a un servicio especifico
    /// (<c>PassengerServiceAssignment</c>). Importa auditarlo porque la asignacion DETERMINA quien
    /// integra el SET del servicio: a quien se le exige nombre/documento al resolver, y quien aparece
    /// en su voucher. El detail JSON lleva <c>serviceType</c>, <c>serviceId</c>, <c>passengerId</c>,
    /// <c>reservaId</c> — NUNCA el numero de documento.
    /// </summary>
    public const string PassengerAssignedToService = "PassengerAssignedToService";

    /// <summary>
    /// ADR-031 v2.1 (2026-06-15): se quito (manualmente) la asignacion de un pasajero a un servicio.
    /// Cambia el SET del servicio, por eso queda trazado quien/cuando. Detail JSON sin numero de documento.
    /// </summary>
    public const string PassengerUnassignedFromService = "PassengerUnassignedFromService";

    /// <summary>
    /// ADR-031 v2.1 (2026-06-15): baja de asignaciones por CASCADA al borrar el servicio (limpieza
    /// transaccional M1). Se distingue de la baja manual (<see cref="PassengerUnassignedFromService"/>)
    /// para que las queries de auditoria sepan que no fue una desasignacion deliberada sino el borrado del
    /// servicio. El detail JSON incluye <c>serviceType</c>, <c>serviceId</c>, <c>removedAssignmentCount</c>.
    /// </summary>
    public const string PassengerUnassignedFromServiceByDelete = "PassengerUnassignedFromServiceByDelete";

    /// <summary>
    /// ADR-031 v2.1 (2026-06-15): REEMPLAZO TOTAL del set de pasajeros de un servicio en UNA operacion
    /// atomica (endpoint PUT .../assignments). En vez de emitir N altas + M bajas sueltas, se audita el
    /// cambio del set como un solo evento con conteos (<c>previousAssignedCount</c>, <c>newAssignedCount</c>,
    /// <c>normalizedToAll</c>). Mas legible y trazable para una operacion bulk. El detail JSON lleva
    /// <c>serviceType</c>, <c>serviceId</c>, <c>reservaId</c> y los conteos — NUNCA numeros de documento ni
    /// nombres. <c>normalizedToAll=true</c> significa que el set pedido era vacio o == todos -> se dejaron
    /// CERO asignaciones (invariante "todos = sin asignaciones").
    /// </summary>
    public const string PassengerAssignmentsReplaced = "PassengerAssignmentsReplaced";

    /// <summary>ADR-031 v2.1: entityName para los eventos sobre la asignacion pasajero &lt;-&gt; servicio.</summary>
    public const string PassengerServiceAssignmentEntityName = "PassengerServiceAssignment";
}
