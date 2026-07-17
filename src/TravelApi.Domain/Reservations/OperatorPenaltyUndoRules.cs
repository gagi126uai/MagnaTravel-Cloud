namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): regla PURA de "cuánto de la multa se cobró
/// realmente" — el monto que, al deshacer la multa, se convierte en saldo a favor del cliente.
///
/// <para><b>Por qué es una regla dedicada y NO reusa <c>ComputePendingPenaltyForDisplay</c></b> (fix del
/// bloqueante de seguridad, invariante de plata): la función de DISPLAY clampea el pendiente a 0 cuando el
/// saldo de la reserva es &lt;= 0 (para no mostrar deuda negativa). Si acuñáramos <c>gross − pendingDisplay</c>,
/// una reserva anulada SALDADA (saldo 0, el caso normal) daría "pendiente 0" → "cobrado = multa entera" →
/// ACUÑARÍA UN CRÉDITO FANTASMA por el total de la multa que el cliente nunca pagó. Esta regla cierra ese
/// agujero: sólo reconoce como "cobrado" lo que un saldo POSITIVO (multa aún por cobrar, convención del
/// display) dejó por debajo del bruto.</para>
///
/// <para><b>Contexto del producto — CORREGIDO (Tanda D1, 2026-07-16)</b>: esta nota decía que "HOY no existe
/// ningún camino para registrar un cobro contra la multa de una reserva anulada". Eso dejó de ser cierto desde
/// el commit <c>44fcea6</c> (2026-07-05): <c>PaymentService.CreatePaymentAsync</c> SÍ admite un camino
/// EXCEPCIONAL y acotado para una reserva <c>Cancelled</c>/<c>PendingOperatorRefund</c> — cobrar (en efectivo o
/// transferencia) contra una Nota de Débito de multa aprobada, con tope su saldo pendiente
/// (<c>PaymentService.EnsureCancelledDebitNoteCollectableAsync</c>, hoy delegado en
/// <c>CancelledDebitNoteCollectionGate</c>). Desde la Tanda D1 existe TAMBIÉN el saldo a favor del cliente
/// aplicado contra esa misma ND (<c>ClientCreditService.ApplyCustomerCreditToPenaltyAsync</c>), sin mover caja.
/// Ninguno de los dos caminos toca <c>Reserva.Balance</c> (ambos setean
/// <c>Payment.AffectsReservaBalance = false</c> a propósito, para no mezclar la deuda fiscal de la ND con la
/// deuda operativa de la reserva ya anulada) — por eso <see cref="ComputeCollectedPenalty"/> (que SÍ mira el
/// saldo de la reserva) sigue sin ver esos cobros y, en la práctica, sigue devolviendo 0 para el caso normal. El
/// guard duro que evita el CRÉDITO FANTASMA cuando hay plata real de por medio ya NO es esta regla: es el
/// bloqueo del Deshacer en <c>BookingCancellationService.EnsureUndoDebitNoteAllowedAsync</c> (Tanda D1, B3), que
/// rechaza deshacer una ND con algún <c>Payment</c> vivo (puente O cobro real) todavía imputado contra ella.</para>
/// </summary>
public static class OperatorPenaltyUndoRules
{
    /// <summary>
    /// Calcula la porción EFECTIVAMENTE COBRADA de una multa, dado su bruto congelado
    /// (<paramref name="grossPenalty"/>) y el saldo de la reserva en la moneda de la multa
    /// (<paramref name="penaltyCurrencyBalance"/>, convención: positivo = multa aún por cobrar).
    ///
    /// <list type="bullet">
    ///   <item>bruto &lt;= 0 → 0 (no hay multa que cobrar).</item>
    ///   <item>saldo &lt;= 0 → 0. La reserva NO tiene un saldo positivo que represente "multa por cobrar":
    ///     no se acuña nada (evita el crédito fantasma; y si hubiera saldo a favor, ese excedente ya se
    ///     convirtió en su propio crédito al cobrarse — re-acuñar acá sería doble).</item>
    ///   <item>0 &lt; saldo &lt; bruto → <c>bruto − saldo</c> (parcialmente cobrada: lo cobrado es el resto).</item>
    ///   <item>saldo &gt;= bruto → 0 (íntegramente por cobrar: nada cobrado todavía).</item>
    /// </list>
    /// </summary>
    public static decimal ComputeCollectedPenalty(decimal grossPenalty, decimal penaltyCurrencyBalance)
    {
        if (grossPenalty <= 0m)
            return 0m;

        if (penaltyCurrencyBalance <= 0m)
            return 0m;

        // Saldo positivo = multa AÚN por cobrar (topeado al bruto, por si el saldo trae residuos de otros
        // conceptos). Lo cobrado es el bruto menos lo que todavía se debe.
        var stillOwed = System.Math.Min(penaltyCurrencyBalance, grossPenalty);
        return grossPenalty - stillOwed;
    }
}
