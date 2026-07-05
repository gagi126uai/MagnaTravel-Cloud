using System;
using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Reglas ÚNICAS de "deuda de una reserva" para las pantallas (chip rojo "Vencida con deuda",
/// alertas de cobranza, contexto de plata en reservas anuladas).
///
/// <para><b>Por qué existe</b>: antes la regla "vencida con deuda" estaba escrita a mano en los dos
/// overloads de <c>ReservaService.ApplyEconomicFlags</c> (listado y detalle) mirando SOLO fecha y saldo,
/// sin mirar el ESTADO de la reserva. Consecuencia (bug auditoría 2026-07-04): una reserva ANULADA vieja
/// con una deuda "congelada" (saldo positivo que nunca se resolvió) mostraba el chip rojo de deuda
/// vencida, como si todavía se le pudiera cobrar al cliente. Una reserva anulada NO tiene cuenta por
/// cobrar genérica: su plata se resuelve por el circuito de cancelación (saldo a favor, multa por
/// anulación con Nota de Débito), no por el cobro normal.</para>
///
/// <para><b>Fuente canónica de estados</b>: el set de estados donde una deuda es cobrable es
/// <see cref="EstadoReserva.SaleFirmStatuses"/> (venta firme: En gestión, Confirmada, Finalizada). Acá NO
/// se copia esa lista: se delega en <see cref="EstadoReserva.IsSaleFirmStatus"/> para no divergir nunca.
/// <c>FinancePositionService.ReceivableDebtStatuses</c> es alias del mismo set (AR / cuenta corriente).</para>
/// </summary>
public static class ReservationDebtRules
{
    /// <summary>
    /// True si, en este ESTADO, una deuda (saldo positivo) es realmente COBRABLE: se muestra chip rojo,
    /// entra al AR / cuenta por cobrar y dispara alertas de cobranza. Es un alias explícito del set canónico
    /// de venta firme (<see cref="EstadoReserva.SaleFirmStatuses"/>), NO una copia.
    ///
    /// <para>Excluye a propósito: pre-venta (Quotation/Budget), descartes (Lost), y los terminales de
    /// cancelación (Cancelled / PendingOperatorRefund) cuya plata se resuelve por el circuito de refund.
    /// También excluye En viaje (Traveling): en prepago puro no se entra a viajar debiendo.</para>
    /// </summary>
    public static bool IsDebtCollectableStatus(string? status) => EstadoReserva.IsSaleFirmStatus(status);

    /// <summary>
    /// Regla ÚNICA del chip "Vencida con deuda". Una reserva figura vencida-con-deuda cuando las TRES
    /// condiciones se cumplen a la vez:
    ///   1. su estado admite deuda cobrable (<see cref="IsDebtCollectableStatus"/>) — una anulada NUNCA;
    ///   2. el viaje ya terminó (la fecha de fin quedó en el pasado);
    ///   3. todavía no está saldada económicamente (le sigue debiendo el cliente).
    ///
    /// <para><c>isEconomicallySettled</c> lo calcula el gate económico canónico (mismo criterio que usa
    /// AFIP): incluye la tolerancia de redondeo y trata un sobrepago / saldo a favor como "no debe". Acá se
    /// recibe ya calculado a propósito: esta regla NO lo recalcula ni lo toca.</para>
    /// </summary>
    public static bool HasOverdueDebt(string? status, DateTime? endDate, bool isEconomicallySettled, DateTime todayUtc)
        => IsDebtCollectableStatus(status)
           && endDate.HasValue
           && endDate.Value.Date < todayUtc.Date
           && !isEconomicallySettled;

    /// <summary>
    /// Contexto de PLATA REAL en una reserva anulada. Una reserva anulada nunca muestra "deuda" genérica
    /// (ver <see cref="HasOverdueDebt"/>): en su lugar muestra, con contexto, la única plata que sí importa.
    /// </summary>
    public enum CancelledMoneyContext
    {
        /// <summary>La reserva anulada quedó en cero: no hay plata pendiente en ningún sentido.</summary>
        None = 0,

        /// <summary>
        /// Quedó plata del CLIENTE sin devolver ni aplicar (saldo a favor pendiente). Se detecta por saldo
        /// negativo: el cliente pagó de más respecto de lo que se le terminó facturando al anular.
        /// </summary>
        ClientCreditPending = 1,

        /// <summary>
        /// La deuda que figura es la MULTA por anulación, cobrable con contexto: hay una Nota de Débito viva
        /// (emitida por la penalidad propia de la agencia) que respalda ese saldo positivo.
        /// </summary>
        PenaltyReceivable = 2,

        /// <summary>
        /// Saldo positivo SIN justificación (no hay Nota de Débito de multa que lo respalde). Es un dato roto:
        /// una reserva anulada no debería tener una cuenta por cobrar sin su comprobante. Lo detectará el
        /// vigía de consistencia (pieza futura, fuera de alcance acá) — se marca para no ocultarlo.
        /// </summary>
        Inconsistent = 3,
    }

    /// <summary>
    /// Deriva el <see cref="CancelledMoneyContext"/> de una reserva anulada a partir de su saldo y de si
    /// existe una Nota de Débito de multa viva.
    ///
    /// <para>Reglas (decisión del dueño, 2026-07-04):
    ///   - saldo &lt; 0  → <see cref="CancelledMoneyContext.ClientCreditPending"/> (plata del cliente sin devolver);
    ///   - saldo &gt; 0 con ND viva → <see cref="CancelledMoneyContext.PenaltyReceivable"/> (la multa, cobrable);
    ///   - saldo &gt; 0 sin ND viva → <see cref="CancelledMoneyContext.Inconsistent"/> (deuda sin respaldo = dato roto);
    ///   - saldo == 0 → <see cref="CancelledMoneyContext.None"/>.</para>
    ///
    /// <para>El saldo "cero" usa una tolerancia de centavo para no marcar como inconsistente un resto de
    /// redondeo de cambio de moneda.</para>
    /// </summary>
    public static CancelledMoneyContext DeriveForCancelled(decimal balance, bool hasOutstandingDebitNote)
    {
        // Tolerancia de redondeo: un resto de centavo (p. ej. por conversión de moneda) NO es deuda ni saldo
        // a favor real. Mismo espíritu que la tolerancia del gate económico canónico.
        const decimal roundingTolerance = 0.01m;

        if (balance < -roundingTolerance)
            return CancelledMoneyContext.ClientCreditPending;

        if (balance > roundingTolerance)
        {
            return hasOutstandingDebitNote
                ? CancelledMoneyContext.PenaltyReceivable
                : CancelledMoneyContext.Inconsistent;
        }

        return CancelledMoneyContext.None;
    }

    /// <summary>
    /// Token del contrato del DTO/front. Los tokens van en CASTELLANO por consistencia con el precedente del
    /// proyecto (<c>collectionStatus</c> emite "SinMovimientos"/"Saldado"/"SaldoAFavor"); el front igual los
    /// traduce a la etiqueta final que ve el usuario. Los nombres del enum C# quedan en inglés (código), solo el
    /// token de salida es en castellano. <c>None</c> se mapea a null: el campo del DTO queda vacío cuando no hay
    /// plata pendiente, para no pintar un chip innecesario.
    /// </summary>
    public static string? ToDtoString(CancelledMoneyContext context) => context switch
    {
        CancelledMoneyContext.ClientCreditPending => "SaldoAFavorPendiente",
        CancelledMoneyContext.PenaltyReceivable => "MultaPorCobrar",
        CancelledMoneyContext.Inconsistent => "Inconsistente",
        _ => null,
    };
}
