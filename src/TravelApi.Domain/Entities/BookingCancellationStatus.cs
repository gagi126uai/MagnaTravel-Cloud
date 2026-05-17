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
}
