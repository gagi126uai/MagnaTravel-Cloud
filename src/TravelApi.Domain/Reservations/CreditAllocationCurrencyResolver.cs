namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-042 §3.3.2 (2026-07-01, DECIDIDA por el contable): reglas puras del reparto del saldo a
/// favor del cliente por moneda cuando se anula una reserva con cobros. Es plata: las reglas son
/// estrictas y estan aca aisladas para poder testearlas sin base ni servicios.
///
/// <para><b>Reglas de negocio</b> (arts. 765/766 CCyC post-DNU 70/2023):
/// <list type="bullet">
///   <item>El saldo a favor sale en la MONEDA DE LA OBLIGACION que el pago cancelo (la factura),
///         NO en la moneda de lo facturado ni prorrateado entre monedas.</item>
///   <item>El monto es lo EFECTIVAMENTE COBRADO-imputado. <b>Tope = cobrado, nunca el monto de la
///         NC</b> (la NC revierte lo facturado; el saldo a favor es un pasivo civil por lo que
///         entro). Jamas mintear plata que no entro.</item>
///   <item>Pagos a cuenta / sin imputar: credito en la moneda en que se pagaron (no cancelaron
///         obligacion, no heredan moneda de factura). En ADR-021 la moneda del pago a cuenta ES su
///         <c>ImputedCurrency</c>, asi que caen naturalmente aca.</item>
///   <item>Monedas SIEMPRE separadas: nunca se suman ni se convierten (multimoneda dura).</item>
/// </list></para>
/// </summary>
public static class CreditAllocationCurrencyResolver
{
    /// <summary>
    /// Resultado de resolver la moneda del credito contra la moneda del reembolso del operador.
    /// </summary>
    public readonly record struct CreditCurrencyDecision(string Currency, bool RequiresManualReview);

    /// <summary>
    /// ADR-042 §3.3.2 (C1(c)): reconcilia la moneda del REEMBOLSO del operador con la moneda de la
    /// OBLIGACION del cliente. El minteo real es 1:1 con lo devuelto (no se inventa FX), pero solo si
    /// la moneda coincide con la de la obligacion. Divergencia -> revision manual (mismo criterio
    /// conservador que el pre-flight de TC=1): nunca mintear en la moneda equivocada.
    ///
    /// <para><b>MEMBRESIA, no correspondencia por servicio</b> (decision consciente, S2 review 2026-07-02):
    /// esta funcion valida que <paramref name="refundCurrency"/> este ENTRE las monedas de obligacion de la
    /// reserva; NO valida que el reembolso de ESTE operador corresponda a la factura de ESE servicio puntual.
    /// El eje operador↔servicio↔factura no esta modelado 1:1 (un operador puede cubrir varios servicios; una
    /// reserva puede tener servicios en varias monedas). La membresia es el guard conservador posible sin
    /// inventar correspondencias; el resto lo resuelve la revision manual.</para>
    ///
    /// <para><b>TOPE del credito (donde vive de verdad)</b>: el monto minteado NO lo acota esta funcion, lo
    /// acota el cap del operator-refund: <c>OperatorRefundReceived.AllocatedAmount &lt;= ReceivedAmount</c>
    /// (CHECK SQL <c>chk_OperatorRefundsReceived_allocated_not_exceeds</c>). Es decir, el saldo a favor del
    /// cliente esta acotado por lo EFECTIVAMENTE DEVUELTO por el operador (el "1:1 con lo devuelto" de C1). En
    /// el caso normal ese devuelto es &lt;= lo cobrado al cliente por construccion (el operador reintegra a lo
    /// sumo lo que la agencia le pago, y la agencia cobra al cliente >= su costo). Por eso NO se agrega un
    /// segundo tope "por lo cobrado" aca: seria redundante en el caso normal y un cambio de politica del
    /// operator-refund (fuera del alcance de C1) en el patologico. La regla "tope = cobrado" de §3.3.2 queda
    /// documentada; su enforcement duro por-moneda es una decision separada del modulo operator-refund.</para>
    ///
    /// <para><b>Caso normal</b> (una factura, servicio en la misma moneda): el reembolso viene en la
    /// moneda de la obligacion -> consistente, minteo 1:1. Es el caso mono-factura, byte-equivalente.</para>
    /// <para><b>Sin obligaciones imputadas</b> (solo pagos a cuenta): no hay obligacion contra la cual
    /// validar -> a cuenta = moneda de pago (= moneda del reembolso), se mintea sin bloquear.</para>
    /// </summary>
    /// <param name="refundCurrency">Moneda del reembolso del operador (ISO: USD/ARS).</param>
    /// <param name="obligationCurrencies">Monedas de las obligaciones del cliente imputadas (ISO).</param>
    public static CreditCurrencyDecision ResolveCreditCurrency(
        string refundCurrency,
        IReadOnlyCollection<string> obligationCurrencies)
    {
        var normalizedRefund = (refundCurrency ?? string.Empty).Trim().ToUpperInvariant();

        // Sin obligaciones imputadas: nada contra que validar (a cuenta = moneda de pago). Minteamos
        // en la moneda del reembolso, sin revision manual.
        if (obligationCurrencies is null || obligationCurrencies.Count == 0)
            return new CreditCurrencyDecision(normalizedRefund, RequiresManualReview: false);

        bool matches = obligationCurrencies
            .Any(c => string.Equals(c?.Trim(), normalizedRefund, StringComparison.OrdinalIgnoreCase));

        if (matches)
            return new CreditCurrencyDecision(normalizedRefund, RequiresManualReview: false);

        // Divergencia: el operador reembolsa en una moneda distinta de la obligacion del cliente. No
        // inventamos un TC ni minteamos en la moneda equivocada -> revision manual.
        return new CreditCurrencyDecision(normalizedRefund, RequiresManualReview: true);
    }
}
