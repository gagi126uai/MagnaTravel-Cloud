namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.3, 2026-05-13): tipo de retiro de saldo cliente
/// (<see cref="ClientCreditWithdrawal"/>). Modela T3 del flujo:
///
///   - <see cref="KeptAsCredit"/>      : el cliente NO retira, deja el saldo
///                                       a su favor (sin caducidad, regla 5 policy).
///   - <see cref="PhysicalCash"/>      : retira en efectivo (limita Ley 25.345, INV-094).
///   - <see cref="Transfer"/>          : retira por transferencia bancaria.
///   - <see cref="AppliedToNewBooking"/>: el saldo se aplica como pago de una nueva reserva.
///   - <see cref="ReversedToOperator"/>: <c>ClientRefundReversal</c> (ADR-002 §2.10) — el
///                                       cliente devuelve dinero ya recibido. Requiere
///                                       <c>ApprovalRequest</c> + audit reforzado.
/// </summary>
public enum WithdrawalKind
{
    /// <summary>Saldo retenido como credito del cliente (no genera <c>ManualCashMovement</c>).</summary>
    KeptAsCredit = 0,

    /// <summary>Retiro fisico en efectivo. Sujeto a umbral Ley 25.345 (INV-094).</summary>
    PhysicalCash = 1,

    /// <summary>Retiro via transferencia bancaria. Metodo recomendado para montos altos.</summary>
    Transfer = 2,

    /// <summary>El saldo se aplica como pago de una reserva nueva del mismo cliente.</summary>
    AppliedToNewBooking = 3,

    /// <summary>Reversal post-T3: el cliente devuelve dinero ya retirado para que se re-acredite al operador. Solo via <c>ApprovalRequest.ClientRefundReversal</c>.</summary>
    ReversedToOperator = 4,
}
