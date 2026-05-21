namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.4, 2026-05-13): maquina de estados del aggregate
/// <see cref="BookingCancellation"/>. Cada transicion esta documentada en la
/// tabla §2.4 del ADR — cualquier cambio aqui implica revisar la tabla y los
/// invariantes Bucket G (INV-081..125).
///
/// IMPORTANTE: la SoT fiscal NO es este enum sino <c>Invoice.AnnulmentStatus</c>.
/// Este enum proyecta el estado operativo del flujo T0..T3 para la UI/servicios
/// y se sincroniza desde <c>BookingCancellationService</c> + <c>ProcessAnnulmentJob</c>.
/// Regla dura: <c>BookingCancellation</c> nunca pasa de <see cref="AwaitingFiscalConfirmation"/>
/// hacia adelante sin que <c>Invoice.AnnulmentStatus = Succeeded</c> (INV-083, sin override).
/// </summary>
public enum BookingCancellationStatus
{
    /// <summary>Borrador iniciado por el vendedor; aun no se confirmo con el cliente. Unica salida ademas del flujo: <see cref="Aborted"/>.</summary>
    Drafted = 0,

    /// <summary>T0 emitido: NC enviada a AFIP, esperando CAE. La Reserva pasa a <c>PendingOperatorRefund</c>.</summary>
    AwaitingFiscalConfirmation = 1,

    /// <summary>T1 confirmado: AFIP devolvio CAE. Falta el T2 (ingreso fisico del operador).</summary>
    AwaitingOperatorRefund = 2,

    /// <summary>T2 ocurrido: hubo al menos un <c>OperatorRefundAllocation</c> contra este BC. El cliente puede empezar a retirar.</summary>
    ClientCreditApplied = 3,

    /// <summary>T3 cerrado: el cliente consumio o transfirio todo el saldo. La Reserva queda en <c>Cancelled</c>.</summary>
    Closed = 4,

    /// <summary>El operador supero el timeout (<c>OperatorRefundTimeoutDays</c>) sin devolver. La Reserva queda en <c>Cancelled</c>. Recovery posible via <c>lateRefundReceived</c>.</summary>
    AbandonedByOperator = 5,

    /// <summary>Abort manual desde <see cref="Drafted"/>: ningun side-effect fiscal generado. La Reserva no cambia.</summary>
    Aborted = 6,

    /// <summary>
    /// FC1.2.1 (2026-05-17): la NC fue enviada a AFIP pero el organismo la
    /// rechazo (CAE no aprobado). Estado terminal para el flujo automatico —
    /// la remediacion es manual: el back-office reintenta la annulacion (corrigiendo
    /// datos), o un Admin usa <c>ForceArcaConfirmationAsync</c> apuntando a una NC
    /// emitida fuera del sistema. La Reserva sigue en su estado previo a T0
    /// (no transiciona a <c>PendingOperatorRefund</c> hasta que ARCA confirme).
    /// </summary>
    ArcaRejected = 7,

    // ============================================================
    // FC1.3 (ADR-009 §2.3.3 / §2.8, 2026-05-21): 4 estados nuevos para
    // el flujo NC parcial. Insertados con valores 8..11 a proposito,
    // sin reutilizar gaps anteriores, para no chocar con la serializacion
    // de los estados existentes ni con queries que asumen rangos viejos.
    // ============================================================

    /// <summary>
    /// FC1.3 (ADR-009): el clasificador identifico que el caso requiere revision
    /// manual, pero el <c>ApprovalRequest</c> todavia no se abrio. Estado
    /// TRANSITORIO: dentro de la misma transaccion, el service pasa directo a
    /// <see cref="ManualReviewPending"/> con el approval creado.
    /// En la practica este valor NO se observa en la BD bajo el flujo normal —
    /// existe en el enum como marker semantico para futuras evoluciones y para
    /// que los tests puedan validar la maquina de estados completa.
    /// </summary>
    RequiresManualReview = 8,

    /// <summary>
    /// FC1.3 (ADR-009): existe un <c>ApprovalRequest</c> tipo
    /// <c>PartialCreditNoteApproval=11</c> abierto (Pending) que espera la
    /// resolucion del admin. El BC esta congelado en este estado hasta que el
    /// admin apruebe o rechace. CHECK SQL garantiza FK al approval no-null.
    /// </summary>
    ManualReviewPending = 9,

    /// <summary>
    /// FC1.3 (ADR-009): el admin aprobo la liquidacion. El siguiente paso es
    /// emitir la NC (en Fase 1 reutiliza el flujo FC1.2 y emite NC por total;
    /// en Fase 2 emitira NC parcial real al ARCA).
    /// </summary>
    ManualReviewApproved = 10,

    /// <summary>
    /// FC1.3 (ADR-009): el admin rechazo la liquidacion. El BC vuelve a
    /// <see cref="Drafted"/> mediante <c>ResetToDraftAsync</c> (Fase 1.3.3) o
    /// se aborta segun decida el vendedor que pidio la revision.
    /// </summary>
    ManualReviewRejected = 11,
}
