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
    // ===== ADR-041 cuentas bancarias polimorficas =====

    /// <summary>
    /// ADR-041 (2026-06-27): se creo una cuenta bancaria (de la Agencia, un Cliente o un Proveedor).
    /// EntityName=BankAccount, EntityId = BankAccount.PublicId. El detail JSON lleva el dueño (tipo+id), la
    /// moneda, el titular y el CBU ENMASCARADO (solo ultimos 4) — NUNCA el CBU completo en el log.
    /// </summary>
    public const string BankAccountCreated = "BankAccountCreated";

    /// <summary>
    /// ADR-041 (2026-06-27): se edito una cuenta bancaria. EntityName=BankAccount, EntityId = PublicId.
    /// El detail JSON lleva el dueño y el CBU enmascarado del estado nuevo — NUNCA el CBU completo.
    /// </summary>
    public const string BankAccountUpdated = "BankAccountUpdated";

    /// <summary>
    /// ADR-041 (2026-06-27): se DESACTIVO (soft-delete) una cuenta bancaria. EntityName=BankAccount,
    /// EntityId = PublicId. El detail JSON lleva el dueño y el CBU enmascarado.
    /// </summary>
    public const string BankAccountDeleted = "BankAccountDeleted";

    /// <summary>
    /// ADR-041 (2026-06-27): alguien ABRIO el detalle de una cuenta bancaria, accediendo al CBU/alias COMPLETOS
    /// (desenmascarados). Es la unica LECTURA que se audita (la lista va enmascarada). Importa para el producto
    /// multi-agencia: deja rastro de quien vio un destino de transferencia. EntityName=BankAccount, EntityId =
    /// PublicId. El detail JSON lleva el dueño y el CBU/alias ENMASCARADOS (no se duplica el dato en claro).
    /// </summary>
    public const string BankAccountDetailViewed = "BankAccountDetailViewed";

    /// <summary>
    /// ADR-041 TANDA 6 (2026-06-28): una cuenta bancaria quedo como PRINCIPAL del dueño para su moneda (a donde
    /// transferir por defecto). Accion SENSIBLE: cambia el destino de pago sugerido. Se registra cuando una cuenta
    /// PASA a ser principal (alta/edicion con IsPrimary=true, endpoint set-primary, o auto-principal de la primera
    /// cuenta del dueño+moneda). EntityName=BankAccount, EntityId=PublicId. El detail lleva dueño, moneda y el CBU
    /// ENMASCARADO — NUNCA el CBU completo.
    /// </summary>
    public const string BankAccountSetPrimary = "BankAccountSetPrimary";

    /// <summary>ADR-041: entityName para los eventos de auditoria sobre una cuenta bancaria.</summary>
    public const string BankAccountEntityName = "BankAccount";

    // ===== ADR-040 cuenta corriente del cliente =====

    /// <summary>
    /// ADR-040 (2026-06-26): cambio de la configuracion de cuenta corriente de un cliente (modo de cobro,
    /// limites de credito por moneda, plazo de pago). Accion SENSIBLE: define cuanta plata se le presta al
    /// cliente. EntityName=Customer, EntityId=Customer.Id; el detalle JSON lleva viejo-&gt;nuevo de cada campo.
    /// </summary>
    public const string CustomerCreditConfigUpdated = "CustomerCreditConfigUpdated";

    /// <summary>
    /// (2026-07-17) Se cambio la CONDICION fiscal del cliente (<c>TaxConditionId</c>/<c>TaxCondition</c> —
    /// Consumidor Final, Monotributo, Responsable Inscripto, Exento) SIN cambiar el CUIT. Decision del dueño:
    /// la condicion es un dato de HOY (a diferencia del CUIT, que es una identidad y una vez que el cliente
    /// facturo no se edita — ver <c>MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync</c>, CODE-06), asi
    /// que se permite editarla SIEMPRE, incluso con facturas vivas, pero queda auditada: el contador necesita
    /// poder ver cuando cambio la condicion de un cliente que ya tiene historia fiscal. EntityName=Customer,
    /// EntityId=Customer.Id. El detail JSON lleva el viejo -&gt; nuevo de TaxConditionId y TaxCondition.
    /// </summary>
    public const string CustomerTaxConditionChanged = "CustomerTaxConditionChanged";

    /// <summary>
    /// (2026-07-17, N1 de la revision) Se cambio el CUIT del cliente (<c>TaxId</c>) — solo se llega a este
    /// evento cuando el cambio fue PERMITIDO (paso <c>MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync</c>,
    /// o el cliente no tenia factura viva). El CUIT es una IDENTIDAD, no un dato de HOY como la condicion (ver
    /// <see cref="CustomerTaxConditionChanged"/>): un cambio legitimo (ej. corregir un typo cargado mal) igual
    /// merece rastro para el contador. EntityName=Customer, EntityId=Customer.Id. El detail JSON lleva el
    /// viejo -&gt; nuevo de TaxId.
    /// </summary>
    public const string CustomerTaxIdChanged = "CustomerTaxIdChanged";

    /// <summary>
    /// (2026-07-17, N1 de la revision) Espejo de <see cref="CustomerTaxIdChanged"/> del lado PROVEEDOR: se
    /// cambio el CUIT del proveedor (<c>TaxId</c>) y el cambio fue PERMITIDO (no habia reservas con factura
    /// viva referenciando al proveedor). EntityName=Supplier, EntityId=Supplier.Id. El detail JSON lleva el
    /// viejo -&gt; nuevo de TaxId.
    /// </summary>
    public const string SupplierTaxIdChanged = "SupplierTaxIdChanged";

    /// <summary>
    /// (2026-07-17) Espejo de <see cref="CustomerTaxConditionChanged"/> del lado PROVEEDOR: se cambio la
    /// CONDICION fiscal del proveedor (<c>TaxCondition</c>) SIN cambiar el CUIT. Mismo criterio: la condicion
    /// del proveedor ni siquiera entra en ningun comprobante de venta (ese lleva los datos del cliente), asi
    /// que editarla nunca compromete un comprobante ya emitido — se permite SIEMPRE, con auditoria. El CUIT del
    /// proveedor sigue bloqueado si hay reservas con factura viva (ver
    /// <c>MutationGuards.GetSupplierTaxIdMutationBlockReasonAsync</c>, CODE-13). EntityName=Supplier,
    /// EntityId=Supplier.Id. El detail JSON lleva el viejo -&gt; nuevo de TaxCondition.
    /// </summary>
    public const string SupplierTaxConditionChanged = "SupplierTaxConditionChanged";

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

    /// <summary>
    /// ADR-042 §3.6 (2026-07-02): un usuario reintento las notas de credito faltantes de una anulacion
    /// multi-factura a medias (endpoint retry-credit-notes). Es una operacion fiscal iniciada por una persona:
    /// deja rastro del actor aunque el retry solo re-encole (la NC sigue en emision).
    /// </summary>
    public const string BookingCancellationCreditNotesRetried = "BookingCancellationCreditNotesRetried";

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
    /// FC4 (saldo a favor del cliente aplicado por el flujo de CUENTA DEL CLIENTE, espejo del lado operador):
    /// se APLICO saldo a favor del cliente a OTRA reserva del mismo cliente y misma moneda desde el endpoint
    /// <c>POST /api/customers/{id}/credit/apply</c>. Drena el/los bolsillos (FIFO) y baja la deuda exigible de
    /// la reserva destino via el Payment puente, SIN mover caja. Se emite UN evento por bolsillo drenado, con
    /// el detail JSON: retiro, bolsillo, cliente, moneda, monto y reserva destino — NUNCA datos sensibles.
    /// Difiere de <see cref="ClientCreditAppliedToBooking"/> (que es el audit del LADO DESTINO del flujo viejo
    /// por-bolsillo): este es el audit del flujo nuevo a nivel cliente, staged en la misma transaccion.
    /// </summary>
    public const string ClientCreditApplied = "ClientCreditApplied";

    /// <summary>
    /// FC4 reversa (2026-06-18): se DESHIZO la aplicacion de un saldo a favor a otra reserva (el caso inverso de
    /// <see cref="ClientCreditAppliedToBooking"/>). El saldo vuelve al bolsillo del cliente (se RE-INCREMENTA el
    /// <c>RemainingBalance</c> del <see cref="ClientCreditEntry"/>) y el <see cref="Payment"/> puente positivo de
    /// la reserva destino queda soft-deleted (la deuda de esa reserva vuelve a su nivel previo). Se usa cuando la
    /// aplicacion fue por error. El detail JSON lleva el withdrawal revertido, el bolsillo, el cliente, la reserva
    /// destino, el monto devuelto al bolsillo y la moneda — NUNCA datos sensibles.
    /// </summary>
    public const string ClientCreditApplicationReversed = "ClientCreditApplicationReversed";

    /// <summary>
    /// Tanda D1 (2026-07-16): se APLICO saldo a favor del cliente contra UNA MULTA (Nota de Debito de una
    /// reserva anulada del mismo cliente), sin mover caja. Se emite UN evento por bolsillo drenado (misma
    /// convencion que <see cref="ClientCreditApplied"/>), staged en la misma transaccion que el retiro + el
    /// Payment puente. El detail JSON lleva withdrawalPublicId, entryPublicId, customerPublicId,
    /// debitNotePublicId, reservaPublicId (la reserva ANULADA dueña de la multa), currency y amount — NUNCA
    /// datos sensibles. La reversa de esta aplicacion reusa <see cref="ClientCreditApplicationReversed"/> (el
    /// puente de multa se revierte con el mismo mecanismo que el puente a otra reserva).
    /// </summary>
    public const string ClientCreditAppliedToPenalty = "ClientCreditAppliedToPenalty";

    /// <summary>
    /// (2026-06-25): se ANULO una reserva en firme SIN factura pero CON cobros vivos (caso (3) del flujo
    /// unificado de "Anular reserva"). La reserva paso a <c>Cancelled</c> y la plata cobrada se convirtio en
    /// SALDO A FAVOR del cliente (un <see cref="ClientCreditEntry"/> por cada moneda con cobros vivos), sin
    /// emitir Nota de Credito (no habia factura que acreditar). Es el camino del medio entre la baja simple
    /// (sin plata) y la anulacion formal con NC (con factura). El detail JSON lleva la reserva, el cliente y
    /// el monto trasladado a saldo a favor POR MONEDA — NUNCA costos ni datos sensibles.
    /// </summary>
    public const string ReservaCancelledWithPaymentsToClientCredit = "ReservaCancelledWithPaymentsToClientCredit";

    /// <summary>
    /// (2026-06-26): se ANULO DIRECTAMENTE una reserva en firme SIN factura y SIN cobros vivos (caso (2)
    /// "DirectCancel" del flujo unificado de "Anular reserva"). La reserva paso a <c>Cancelled</c> sin generar
    /// ningun saldo a favor (no habia plata que trasladar) ni emitir Nota de Credito (no habia factura que
    /// acreditar). Es la baja directa. Se separa de <see cref="ReservaCancelledWithPaymentsToClientCredit"/>
    /// para que la auditoria NO insinue un "saldo a favor" inexistente. El detail JSON lleva la reserva, el
    /// estado origen, el destino (Cancelled) y el motivo — la lista de saldos a favor viaja VACIA.
    /// </summary>
    public const string ReservaAnnulledDirectlyWithoutCredit = "ReservaAnnulledDirectlyWithoutCredit";

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

    /// <summary>
    /// 2026-06-28 (Fase A — cierre sin multa): un usuario confirmo que el OPERADOR NO COBRO MULTA (devuelve todo)
    /// y cerro la pata de la penalidad de la cancelacion SIN emitir Nota de Debito. Es una DECISION DE NEGOCIO
    /// explicita, por eso es OBLIGATORIO este rastro: permite al contador distinguir "el operador no cobro multa"
    /// (real) de "penalidad = 0 por error". El detail JSON lleva quien/cuando, la referencia del BC y la reserva,
    /// y el motivo que el usuario indico. NO mueve plata ni emite comprobante fiscal.
    /// </summary>
    public const string OperatorPenaltyWaived = "OperatorPenaltyWaived";

    /// <summary>
    /// 2026-06-28 (Fase A — reversa del cierre sin multa): un ADMINISTRADOR REABRIO un cierre sin multa
    /// (<c>PenaltyStatus.Waived</c> -> <c>Estimated</c>), volviendo a dejar la penalidad del operador pendiente de
    /// resolver. Existe porque el cierre sin multa es terminal y, sin esta reversa, un error de carga o una multa
    /// tardia del operador no tendria forma de corregirse desde el sistema. Es una accion sensible y poco habitual:
    /// solo Admin, y este rastro es OBLIGATORIO (el contador debe poder ver quien reabrio, cuando y por que). El
    /// detail JSON lleva quien/cuando, la referencia del BC y la reserva, y el motivo. NO mueve plata ni emite
    /// comprobante fiscal (el waive no habia emitido ninguno).
    /// </summary>
    public const string OperatorPenaltyWaiveReverted = "OperatorPenaltyWaiveReverted";

    /// <summary>
    /// Spec "el paso de multa vive en la ficha" (A4, 2026-07-08): un usuario CORRIGIO el monto + moneda de una
    /// multa YA CONFIRMADA cuya Nota de Debito habia quedado trabada (revision manual por moneda distinta, o
    /// fallida) y SIN comprobante emitido con CAE. Es una accion fiscalmente sensible (cambia el numero que va a
    /// una ND que se re-encola), por eso lleva su PROPIA accion de auditoria (no se mezcla con la emision normal
    /// de ND) para que el contador la pueda filtrar. El detail JSON lleva quien/cuando, la referencia del BC y la
    /// reserva, el motivo, y el antes/despues de monto y moneda.
    /// </summary>
    public const string OperatorPenaltyCorrected = "OperatorPenaltyCorrected";

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): un usuario SOLICITO deshacer una Nota de Debito de
    /// multa ya emitida con CAE (arma la Nota de Credito que la anula). Accion fiscalmente sensible: propia
    /// accion de auditoria (no se mezcla con la emision normal de ND) para que el contador la pueda filtrar. El
    /// detail JSON lleva quien/cuando, motivo, comprobantes vinculados (ND anulada, NC creada), importe/moneda/TC.
    /// </summary>
    public const string OperatorPenaltyDebitNoteUndoRequested = "OperatorPenaltyDebitNoteUndoRequested";

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida": la Nota de Credito que anula la ND CONSIGUIO su CAE — la ND queda
    /// desvinculada del BC (el paso vuelve a abierto) y, si la multa estaba cobrada (total o parcial), se acuño
    /// saldo a favor del cliente por la porcion cobrada. El detail JSON lleva el efecto en la plata.
    /// </summary>
    public const string OperatorPenaltyDebitNoteUndone = "OperatorPenaltyDebitNoteUndone";

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (corner "Succeeded sin mint", 2026-07-14): la Nota de Crédito que
    /// anula la ND consiguió CAE, pero el BC ya NO apuntaba a esa ND (otro flujo la re-apuntó en carrera). Se
    /// marcó el evento como consumado (el hecho fiscal es real) pero NO se desvinculó ni se acuñó saldo a favor:
    /// requiere revisión manual. Auditoría dedicada para que el salto no quede silencioso.
    /// </summary>
    public const string OperatorPenaltyDebitNoteUndoNeedsReview = "OperatorPenaltyDebitNoteUndoNeedsReview";

    /// <summary>
    /// ADR-044 (fix choque con ADR-014, 2026-07-14): el re-vinculador de ND huerfana (2026-07-08) es MAS VIEJO
    /// que el flujo de "deshacer una multa" (2026-07-14). El perfil que usa ese re-vinculador para detectar una
    /// ND huerfana por crash ("BC Confirmed sin ND vinculada") es EXACTAMENTE el mismo perfil que deja un BC
    /// recien deshecho a proposito — asi que, sin distinguirlos, el re-vinculador podia re-enganchar una ND que
    /// ya habia sido anulada por su propia Nota de Credito, dejando el paso otra vez como "multa cobrada" sin
    /// salida (limbo real de produccion, reserva F-2026-1043 / BC 13). Esta accion audita la AUTO-REPARACION
    /// que la bandeja de NDs corre al detectar ese estado corrupto: desvincula de nuevo, sin acuñar saldo a
    /// favor otra vez (ya se acuñó en la reconciliación original).
    /// </summary>
    public const string OperatorPenaltyDebitNoteOrphanLinkRepaired = "OperatorPenaltyDebitNoteOrphanLinkRepaired";

    /// <summary>
    /// ADR-044 T5-emision (2026-07-15): un usuario CONFIRMO y disparo la emision real de la Nota de Credito
    /// parcial de un servicio cancelado (queda encolada, esperando CAE). El detail JSON lleva la factura destino,
    /// el monto congelado a acreditar y la moneda. Distinta de <see cref="BookingCancellationConfirmed"/> (esa es
    /// la anulacion TOTAL, T0 del flujo legacy): esta accion NUNCA marca la reserva ni la factura como anuladas.
    /// </summary>
    public const string PartialCreditNoteEmissionRequested = "PartialCreditNoteEmissionRequested";

    /// <summary>
    /// ADR-044 T5-emision (2026-07-15): la Nota de Credito parcial de un servicio cancelado CONSIGUIO su CAE. El
    /// detail JSON lleva el efecto en la plata (reversion economica) y si la factura destino quedo totalmente
    /// acreditada (ultima porcion) o sigue viva por el resto.
    /// </summary>
    public const string PartialCreditNoteEmitted = "PartialCreditNoteEmitted";

    /// <summary>
    /// ADR-044 T5-emision (2026-07-15): AFIP RECHAZO la Nota de Credito parcial de un servicio cancelado. La
    /// hija queda Failed, la factura destino NUNCA se tocó (sigue viva) y el BC vuelve a Drafted para que el
    /// back-office pueda reintentar desde el mismo paso.
    /// </summary>
    public const string PartialCreditNoteEmissionRejected = "PartialCreditNoteEmissionRejected";

    /// <summary>
    /// ADR-044 T2 Addendum (2026-07-10): un usuario agrego un cargo SECUNDARIO del operador sobre una multa ya
    /// confirmada (ej. una retencion fiscal ademas del cargo administrativo automatico). Accion OPCIONAL, no
    /// parte del flujo simple. El detail JSON lleva quien/cuando, el BC/reserva, el operador, y el
    /// Kind/CollectionMode/Amount/Currency/DocumentRef del cargo agregado.
    /// </summary>
    public const string OperatorChargeAdded = "OperatorChargeAdded";

    /// <summary>
    /// (2026-06-26): el operador supero el plazo (<c>OperatorRefundDueBy</c>) sin reembolsar. El job nocturno
    /// transiciono la cancelacion <c>AwaitingOperatorRefund</c> -> <c>AbandonedByOperator</c> y cerro la RESERVA
    /// (<c>PendingOperatorRefund</c> -> <c>Cancelled</c>). Antes este estado nunca se asignaba (codigo muerto) y
    /// la cuenta por cobrar al operador quedaba colgada sin alerta. Disparado por el sistema (sin actor humano).
    /// <b>AbandonedByOperator es terminal por ahora</b>: registrar un reembolso tardio sobre una BC ya abandonada
    /// NO esta implementado (queda como follow-up futuro); hoy ese caso se resuelve a mano.
    /// </summary>
    public const string BookingCancellationAbandonedByOperator = "BookingCancellationAbandonedByOperator";

    /// <summary>
    /// (2026-07-03): una anulacion quedo firme (NC total con CAE) pero el operador NO tiene NADA que devolver
    /// (receivable vivo $0 en TODAS las monedas — tipico cuando la agencia nunca le pago nada al operador por ese
    /// viaje). En vez de dejarla colgada en <c>AwaitingOperatorRefund</c> para siempre (un limbo: no se puede
    /// registrar reembolso porque no hay plata a recibir, ni pagar al operador porque la reserva ya esta anulada),
    /// se cierra DIRECTO: BC <c>AwaitingOperatorRefund</c>/<c>AbandonedByOperator</c> -> <c>Closed</c> y la RESERVA
    /// <c>PendingOperatorRefund</c> -> <c>Cancelled</c>. Se dispara en la transicion post-CAE (la reserva nace sin
    /// receivable) o en el barrido nocturno (cierra las que ya quedaron trabadas asi). NO mueve plata ni emite/anula
    /// comprobantes fiscales (la NC ya salio). El detail JSON lleva la cancelacion, la reserva, <c>zeroReceivable</c>
    /// y el <c>origin</c> (transicion|barrido) — NUNCA datos sensibles. NO se cierra si la multa del operador sigue
    /// pendiente de gestion (su Nota de Debito puede tener que emitirse primero).
    /// </summary>
    public const string BookingCancellationClosedNoOperatorRefundDue = "BookingCancellationClosedNoOperatorRefundDue";

    /// <summary>
    /// ADR-041 TANDA 4 (2026-06-28): se REABRIO una cancelacion <c>AbandonedByOperator</c> para registrar un
    /// REEMBOLSO TARDIO del operador (el operador devolvio plata DESPUES de que el plazo venció y la cuenta se
    /// dio por perdida). La cancelacion vuelve a <c>AwaitingOperatorRefund</c> con un nuevo plazo; la RESERVA NO
    /// se resucita (el viaje sigue cancelado), el reembolso tardio solo genera saldo a favor del cliente cuando se
    /// imputa (circuito normal de allocation). El detail JSON lleva la cancelacion, la reserva, el estado previo,
    /// el nuevo plazo y el motivo — NUNCA datos sensibles. Disparado por caja (mismo permiso que registrar el
    /// reembolso). Es la marca durable de que ese reembolso fue "tardio".
    /// </summary>
    public const string BookingCancellationReopenedForLateRefund = "BookingCancellationReopenedForLateRefund";

    // ===== Modulo SupplierCredit (ADR-041 TANDA 3, lado proveedor) =====

    /// <summary>
    /// ADR-041 TANDA 3 (2026-06-27): nacio (o se agrando) un saldo a favor consumible con un operador
    /// porque un pago al operador genero sobrepago en una moneda (<see cref="SupplierCreditEntryEntityName"/>).
    /// El detail JSON lleva el operador, la moneda, el monto acreditado y el pago de origen — NUNCA datos
    /// sensibles. Disparado dentro de la misma transaccion que el recalculo de la deuda del proveedor.
    /// </summary>
    public const string SupplierCreditCreated = "SupplierCreditCreated";

    /// <summary>
    /// ADR-041 TANDA 3 (2026-06-27): se REDUJO/destruyo saldo a favor con un operador porque el pago de origen se
    /// edito o borro y el sobrepago derivado bajo. El reconciler drena el credito NO aplicado (en lockstep
    /// CreditedAmount/RemainingBalance). El detail JSON lleva el operador, la moneda, el monto drenado y el pago
    /// de origen. Si el drenaje requeriria tocar credito ya aplicado a otra reserva, la operacion se bloquea (no
    /// hay drenaje). Cierra el agujero de que solo se auditaba la creacion del saldo a favor, no su reduccion.
    /// </summary>
    public const string SupplierCreditDrained = "SupplierCreditDrained";

    /// <summary>
    /// ADR-041 TANDA 3 (2026-06-27): se APLICO saldo a favor del operador a OTRA reserva del mismo operador y
    /// misma moneda (drena el pool, baja la deuda-por-reserva del destino, NETO-CERO sobre el Balance agregado).
    /// El detail JSON lleva el operador, la moneda, el monto, la reserva destino y el bolsillo consumido.
    /// </summary>
    public const string SupplierCreditApplied = "SupplierCreditApplied";

    /// <summary>
    /// ADR-041 TANDA 3 (2026-06-27): se REVIRTIO una aplicacion de saldo a favor del operador (contra-fila
    /// inmutable): repone el pool y deshace la imputacion en la reserva destino. El detail JSON lleva la
    /// aplicacion original, el monto repuesto, la reserva destino y el motivo — NUNCA datos sensibles.
    /// </summary>
    public const string SupplierCreditApplicationReversed = "SupplierCreditApplicationReversed";

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

    /// <summary>ADR-041 TANDA 3: entityName para eventos sobre el saldo a favor con un operador (entry).</summary>
    public const string SupplierCreditEntryEntityName = "SupplierCreditEntry";

    /// <summary>ADR-041 TANDA 3: entityName para eventos sobre la aplicacion/reversa de saldo a favor del operador.</summary>
    public const string SupplierCreditApplicationEntityName = "SupplierCreditApplication";

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

    /// <summary>T5: se resolvió manualmente factura+monto de una NC parcial legacy ambigua.</summary>
    public const string PartialCreditNoteLegacyResolved = "PartialCreditNoteLegacyResolved";

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

    // ===== Reprogramar viaje (2026-06-23) =====

    /// <summary>
    /// REPROGRAMAR VIAJE (2026-06-23): se desplazaron en bloque las fechas de TODOS los servicios de una
    /// reserva (el operador "corrio" el viaje N dias). Importa auditarlo porque cambia las fechas de cabecera
    /// de la reserva (StartDate/EndDate), que mueven el lifecycle (el job que pasa Confirmada -&gt; En viaje
    /// compara StartDate contra hoy). EntityName=Reserva, EntityId = Reserva.Id (legacy int). El detail JSON
    /// lleva <c>daysShift</c>, <c>servicesMoved</c> y las nuevas fechas — NUNCA montos ni datos sensibles.
    /// </summary>
    public const string ReservaRescheduled = "ReservaRescheduled";

    /// <summary>Reprogramar viaje: entityName para el evento de desplazamiento de fechas.</summary>
    public const string ReservaEntityName = "Reserva";

    // ===== Admin auto-autorizado (bypass de 4-eyes, 2026-06-24) =====

    /// <summary>
    /// 2026-06-24: el Admin ejecuto DIRECTO una accion que normalmente exige doble firma (4-eyes / approval),
    /// SIN crear un ApprovalRequest, porque hoy el dueno es el unico Admin y pedirse permiso a si mismo es
    /// teatro (se auto-aprobaba). El bypass esta condicionado SOLO al rol Admin: el dia que existan varios
    /// admins que no sean el dueno, se puede volver a exigir 4-eyes por policy (la maquinaria de approval NO
    /// se borro, sigue intacta para los no-Admin).
    ///
    /// <para><b>Por que este audit es OBLIGATORIO</b>: el Admin no pide permiso, pero el contador necesita el
    /// rastro de que el Admin se auto-autorizo. El detail JSON lleva SIEMPRE: <c>bypassedGate</c> (que barrera
    /// se salteo, ej. "ConfirmPenaltyFourEyes"), <c>entityName</c>/<c>entityId</c> de la entidad afectada, el
    /// <c>reason</c> (motivo no vacio, exigido al Admin), y <c>amount</c>/<c>currency</c> cuando aplica. NUNCA
    /// datos sensibles (documentos, datos de pago).</para>
    /// </summary>
    public const string AdminSelfAuthorized = "AdminSelfAuthorized";
}
