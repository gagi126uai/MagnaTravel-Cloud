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
/// <para><b>Contexto del producto (investigación 2026-07-14)</b>: HOY no existe ningún camino para registrar
/// un cobro contra la multa de una reserva anulada — el alta de cobro (<c>PaymentService.CreatePaymentAsync</c>
/// / <c>ReservaService.AddPaymentAsync</c>) exige <c>EnsureCollectable</c> → estado de venta firme, que EXCLUYE
/// Cancelled/PendingOperatorRefund. Por eso, en la práctica, <see cref="ComputeCollectedPenalty"/> devuelve 0
/// (la reserva anulada queda con saldo &lt;= 0) y el deshacer NO acuña nada. La rama "pagada" es defensiva para
/// un futuro camino de cobro; y es COHERENTE con ese futuro: si algún día un cobro de multa dejara saldo a
/// favor (saldo &lt; 0), ese cobro YA acuña su propio crédito de sobrepago
/// (<c>ConvertOverpaymentToClientCreditAsync</c>), así que acá devolver 0 evita el DOBLE crédito.</para>
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
