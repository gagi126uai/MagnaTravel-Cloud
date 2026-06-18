using System;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

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

    // =====================================================================================================
    // FC4 REVERSA (2026-06-18): deshacer la aplicacion de un saldo a favor a otra reserva.
    //
    // El caso normal (HandleAppliedToNewBookingAsync) saca plata del bolsillo y la pone como pago-puente en
    // la reserva destino. Si se hizo por error, ESTO lo deshace: vuelve la plata al bolsillo y soft-deletea
    // el puente. Mismo patron y misma filosofia "sin SaveChanges" que OverpaymentCreditCleanup: el caller
    // (ClientCreditService.ReverseAppliedCreditAsync) atomiza dentro de su transaccion envolvente.
    // =====================================================================================================

    /// <summary>
    /// FC4 reversa: encuentra el <see cref="Payment"/> puente VIVO (no soft-deleted) de un withdrawal de
    /// aplicacion. Devuelve <c>null</c> si no hay puente vivo (caso tipico: ya se reverso antes -> el puente
    /// quedo soft-deleted). El predicado replica EXACTAMENTE <see cref="IsAppliedCreditBridge"/> pero en
    /// forma traducible a SQL (compara solo columnas escalares) y filtrando por el withdrawal.
    /// </summary>
    public static Task<Payment?> FindLiveBridgeAsync(
        AppDbContext db,
        int withdrawalId,
        CancellationToken ct = default)
    {
        return db.Payments
            .FirstOrDefaultAsync(p =>
                p.AppliedFromCreditWithdrawalId == withdrawalId &&
                p.Method == BridgeMethod &&
                !p.AffectsCash &&
                !p.IsDeleted, ct);
    }

    /// <summary>
    /// FC4 reversa: devuelve el motivo de BLOQUEO (mensaje de negocio en espanol) si NO se puede revertir esta
    /// aplicacion de saldo a favor, o <c>null</c> si la reversa es segura. El caller DEBE chequear esto y
    /// abortar antes de mutar nada.
    ///
    /// <para><b>Guardas de integridad</b> (es plata):
    /// <list type="bullet">
    ///   <item><b>Anti doble-reversa / idempotencia</b>: si el puente ya esta soft-deleted (o nunca existio),
    ///         no hay nada que revertir. La presencia de un puente VIVO es la unica fuente de verdad de "esta
    ///         aplicacion sigue activa": <see cref="ClientCreditWithdrawal"/> no tiene flag de reversado.</item>
    ///   <item><b>Tope superior del bolsillo</b>: re-incrementar <c>RemainingBalance</c> no puede superar el
    ///         <c>CreditedAmount</c> original del entry (respeta el CHECK <c>chk_remaining_non_negative</c> y su
    ///         tope superior). Si ya esta intacto (Remaining == Credited) hay una incoherencia previa -> bloquea.</item>
    /// </list></para>
    ///
    /// <para>El <paramref name="entry"/> y el <paramref name="liveBridge"/> ya cargados los pasa el caller para
    /// no re-consultar. Si <paramref name="liveBridge"/> es null -> ya reversado / sin puente.</para>
    /// </summary>
    public static string? GetReverseBlockReason(
        ClientCreditEntry entry,
        Payment? liveBridge)
    {
        // Anti doble-reversa: sin puente vivo, no hay aplicacion activa que deshacer.
        if (liveBridge is null)
        {
            return "Esta aplicacion de saldo a favor ya fue revertida o no tiene un pago puente activo. " +
                   "No hay nada que deshacer.";
        }

        // Tope superior: la plata que vuelve al bolsillo (monto del puente) no puede dejar el saldo restante
        // por encima del monto originalmente acreditado. Si esto se violara, hay una incoherencia previa
        // (doble acreditacion) y no compensamos a ciegas.
        var remainingAfter = entry.RemainingBalance + liveBridge.Amount;
        if (remainingAfter > entry.CreditedAmount)
        {
            return "No se puede revertir: el monto a devolver al saldo a favor superaria el total acreditado " +
                   "originalmente. Revisar manualmente, hay una inconsistencia en el saldo.";
        }

        return null;
    }

    /// <summary>
    /// FC4 reversa: ejecuta la reversa sobre las entidades ya cargadas (sin <c>SaveChanges</c>; el caller
    /// atomiza). Devuelve el monto re-incrementado (= monto del puente) para que el caller lo audite.
    ///
    /// <para><b>Precondicion</b>: el caller DEBE haber chequeado <see cref="GetReverseBlockReason"/> y abortado
    /// si devolvio motivo. Aca asumimos que el puente esta vivo y el tope no se viola.</para>
    ///
    /// <para><b>Que hace</b>:
    /// <list type="number">
    ///   <item>Soft-deletea el puente (<c>IsDeleted=true</c>, <c>DeletedAt=now</c>). Asi
    ///         <c>AccumulatePayments</c> deja de contarlo y la deuda de la reserva destino vuelve a su nivel.</item>
    ///   <item>Re-incrementa <c>RemainingBalance</c> del entry por el monto del puente (la plata vuelve al
    ///         bolsillo) y limpia <c>IsFullyConsumed</c> (el saldo deja de estar agotado).</item>
    /// </list></para>
    /// </summary>
    public static decimal ReverseArtifacts(
        ClientCreditEntry entry,
        Payment liveBridge)
    {
        var amountReturnedToPocket = liveBridge.Amount;

        // 1) Soft-delete del puente (mismo trato que el sistema le da a los puentes: NUNCA por el path manual
        //    bloqueado, sino directo sobre la entidad — igual que OverpaymentCreditCleanup).
        liveBridge.IsDeleted = true;
        liveBridge.DeletedAt = DateTime.UtcNow;

        // 2) La plata vuelve al bolsillo del cliente.
        entry.RemainingBalance += amountReturnedToPocket;

        // Si volvio a haber saldo, el bolsillo ya no esta agotado.
        if (entry.RemainingBalance > 0m)
        {
            entry.IsFullyConsumed = false;
        }

        return amountReturnedToPocket;
    }
}
