using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Regla UNICA y PURA para "cuanto le queda pendiente de cobro a una Nota de Debito (ND) de multa": su importe
/// total, menos las Notas de Credito que la anularon (parcial o total), menos lo que ya se cobro contra ella.
///
/// <para><b>Por que existe</b> (TANDA C "la multa cobrada se ve cerrada", 2026-07-16): esta cuenta se hacia
/// suelta en tres lugares con las mismas cuatro consultas EF repetidas a mano
/// (<c>PaymentService.EnsureCancelledDebitNoteCollectableAsync</c> al validar un cobro nuevo,
/// <c>CustomerService.BuildPendingPenaltiesAsync</c> al armar la bandeja de multas del cliente, y
/// <c>ReservaService</c> al pintar el cartel de la ficha/listado). El cartel de la ficha usaba ADEMAS una
/// formula DISTINTA — el saldo de la reserva (balance-based) — que desde el 2026-07-15 dejo de ver los cobros
/// reales de multa (<c>Payment.AffectsReservaBalance=false</c> en una reserva anulada, para no mezclar la
/// deuda fiscal de la ND con la deuda operativa de la reserva). Centralizar la formula aca evita que las
/// lecturas vuelvan a divergir.</para>
///
/// <para>Las consultas batcheadas que traen <paramref name="creditedAmount"/> (Notas de Credito vivas
/// asociadas) y <paramref name="collectedAmount"/> (pagos vivos imputados a la ND) viven en Infraestructura
/// (<c>DebitNoteOutstandingLookup</c>, porque necesitan EF); esta funcion solo hace la cuenta, sin tocar la
/// base, para que sea testeable con numeros sueltos.</para>
/// </summary>
public static class DebitNoteOutstandingRules
{
    /// <summary>
    /// Calcula el saldo pendiente de la ND, redondeado a centavos. Puede dar NEGATIVO si se cobro o acredito de
    /// mas (dato crudo a proposito: cada caller decide que hacer con eso — el guard de cobro lo trata como "sin
    /// saldo" (menor o igual a 0), el cartel de la ficha lo topea visualmente a 0 para no mostrar "deuda
    /// negativa").
    /// </summary>
    public static decimal ComputeOutstanding(decimal debitNoteTotal, decimal creditedAmount, decimal collectedAmount)
        => ReservationEconomicPolicy.RoundCurrency(debitNoteTotal - creditedAmount - collectedAmount);
}
