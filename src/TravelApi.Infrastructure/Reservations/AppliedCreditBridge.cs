using System;
using System.Linq.Expressions;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// FC4 (saldo a favor aplicado a otra reserva, 2026-06-14): helper hermano de
/// <see cref="OverpaymentCreditCleanup"/> para el OTRO puente de Payment del sistema.
///
/// <para><b>Por que existe</b>: cuando un saldo a favor del cliente (<see cref="ClientCreditEntry"/>) se
/// aplica como pago de OTRA reserva (kind <c>AppliedToNewBooking</c>), el sistema crea un <see cref="Payment"/>
/// "puente" POSITIVO en la reserva destino, con <c>Method = </c><see cref="BridgeMethod"/>,
/// <c>AffectsCash = false</c> y <see cref="Payment.AppliedFromCreditWithdrawalId"/> apuntando al withdrawal
/// que lo origino. Ese puente lo cuenta <c>ReservaMoneyCalculator.AccumulatePayments</c> (suma los pagos vivos
/// sin mirar AffectsCash), por lo que baja la deuda exigible de la reserva destino — sin tocar caja, porque la
/// plata ya entro cuando el operador devolvio el refund. La caja NO se mueve dos veces.</para>
///
/// <para><b>Por que es un helper separado de OverpaymentCreditCleanup</b>: son dos puentes con semantica y
/// firma distintas. El de sobrepago saca el excedente de una reserva sobrepagada (monto NEGATIVO, atado por
/// <see cref="Payment.OriginalPaymentId"/>); este aplica saldo a favor a una reserva con deuda (monto
/// POSITIVO, atado por <see cref="Payment.AppliedFromCreditWithdrawalId"/>). Mantenerlos separados evita que
/// las guardas se confundan entre si.</para>
/// </summary>
public static class AppliedCreditBridge
{
    /// <summary>Method del Payment puente de aplicacion (no mueve caja, baja la deuda de la reserva destino).</summary>
    public const string BridgeMethod = "SaldoAFavorAplicado";

    /// <summary>
    /// Mensaje de negocio cuando un usuario intenta borrar o editar DIRECTAMENTE el Payment puente de un saldo
    /// a favor aplicado. El puente es respaldo interno: cambiarlo a mano desincroniza el bolsillo del cliente
    /// respecto de la deuda que pago en la reserva destino. Se gestiona desde el saldo a favor, no desde el pago.
    /// </summary>
    public const string DirectBridgeMutationBlockReason =
        "Este movimiento es el respaldo interno de un saldo a favor aplicado a esta reserva; " +
        "gestionalo desde el saldo a favor del cliente.";

    /// <summary>
    /// FC4: true si <paramref name="payment"/> ES el Payment puente de un saldo a favor aplicado.
    ///
    /// <para>La firma es exacta: <c>Method == BridgeMethod</c> + <c>AffectsCash == false</c> (lo distingue de
    /// un cobro real) + <c>AppliedFromCreditWithdrawalId != null</c> (lo ata al withdrawal; lo distingue del
    /// puente de sobrepago y del de NC).</para>
    ///
    /// <para><b>OJO</b>: esto NO se traduce a SQL (recibe la entidad ya materializada). Para filtrar en queries
    /// EF usar la forma inline expandida o <see cref="IsInternalBridgePredicate"/>.</para>
    /// </summary>
    public static bool IsAppliedCreditBridge(Payment payment)
    {
        return payment.Method == BridgeMethod
            && !payment.AffectsCash
            && payment.AppliedFromCreditWithdrawalId != null;
    }

    /// <summary>
    /// FC4: predicado UNICO, traducible a SQL, que excluye AMBOS puentes internos de las listas de pagos
    /// visibles (el de sobrepago de <see cref="OverpaymentCreditCleanup"/> y el de aplicacion de aca).
    ///
    /// <para><b>Por que centralizarlo</b>: antes de FC4 cada lista de pagos repetia inline el predicado del
    /// puente de sobrepago. Al sumar un segundo puente, repetir DOS condiciones en 6+ sitios invita a que
    /// alguno quede desincronizado (se agrega un puente y se olvida una pantalla -> filtra plata interna al
    /// usuario). Concentrando el "es puente interno" en una sola Expression, los 6 sitios la reusan y no pueden
    /// divergir. EF la traduce a SQL porque solo compara columnas escalares (Method/AffectsCash/FKs).</para>
    ///
    /// <para>Devuelve true cuando el pago ES un puente interno (de sobrepago O de aplicacion). Los call-sites
    /// la usan negada (<c>.Where(p =&gt; !AppliedCreditBridge.IsInternalBridge(p))</c> via la version compilada,
    /// o expandiendo esta condicion).</para>
    /// </summary>
    public static Expression<Func<Payment, bool>> IsInternalBridgePredicate =>
        p => (p.Method == OverpaymentCreditCleanup.BridgeMethod && !p.AffectsCash && p.OriginalPaymentId != null)
          || (p.Method == BridgeMethod && !p.AffectsCash && p.AppliedFromCreditWithdrawalId != null);
}
