using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-022 GAP C — fix de correctitud (2026-08-19): resuelve, para un lote de
/// <see cref="OperatorRefundReceived"/>, cuanta plata VIGENTE tiene cada uno en el Libro de Caja
/// (<see cref="CashLedgerEntry"/> con <c>SourceType=OperatorRefund</c>, ni reversado ni reversa).
///
/// <para><b>Por que existe este helper aparte (y no un query inline en cada caller)</b>: el ingreso fisico
/// de un reembolso de operador se asienta SIEMPRE via <c>ManualCashMovement</c>
/// (<c>OperatorRefundService.RecordReceivedInternalAsync</c> -&gt;
/// <c>ManualCashMovementBuilder.BuildIncomeForRefund</c> -&gt; <c>CashLedgerEntryFactory.ForManualMovement</c>).
/// Ese camino deja <c>CashLedgerEntry.ManualCashMovementId</c> poblado (el CHECK SQL
/// <c>chk_cashledger_exactly_one_source</c> exige EXACTAMENTE un FK de origen no-null) y
/// <c>CashLedgerEntry.OperatorRefundReceivedId</c> queda NULL — aunque el esquema tiene esa columna
/// lista para un eventual camino de escritura DIRECTO que hoy no existe en produccion.</para>
///
/// <para><b>Bug que corrige (encontrado 2026-08-19, no forma parte del pedido original de esta obra pero
/// es load-bearing para ella)</b>: el primer <c>CashLedgerRefundReconciliationJob</c> (2026-08-16) filtraba
/// SOLO por <c>e.OperatorRefundReceivedId != null</c>, que en Postgres real siempre da vacio para este
/// origen (el camino de escritura real solo pone <c>ManualCashMovementId</c>). Resultado: el job creia que
/// la caja SIEMPRE tenia $0 vigente para cualquier reembolso, y marcaba divergencia en TODOS los reembolsos
/// ya registrados con monto &gt; 0 — el falso positivo masivo detras del "banner naranja + notificacion
/// URGENTE" que motivo esta obra. Este helper contempla los DOS caminos posibles (el directo, por si algun
/// dia se usa, y el real via ManualCashMovement) para no repetir el bug en ningun consumidor futuro.</para>
/// </summary>
public static class CashLedgerRefundLedgerAmountLoader
{
    /// <summary>
    /// Suma de caja VIGENTE por <c>OperatorRefundReceived.Id</c>. Un refund sin ningun asiento vigente
    /// (revertido del todo, o nunca asentado) simplemente NO aparece en el diccionario — el caller debe
    /// tratar la ausencia como 0, no como "no hay que evaluarlo".
    /// </summary>
    public static async Task<Dictionary<int, decimal>> LoadAsync(
        AppDbContext db, IReadOnlyCollection<int> refundIds, CancellationToken ct)
    {
        if (refundIds.Count == 0)
            return new Dictionary<int, decimal>();

        var rows = await db.CashLedgerEntries
            .Where(e => e.SourceType == CashLedgerSourceTypes.OperatorRefund && !e.IsReversed && !e.IsReversal)
            .Select(e => new
            {
                // Coalesce de los DOS caminos posibles (ver el XML-doc de la clase): el directo (columna
                // propia, sin uso hoy) y el real (via el ManualCashMovement que asento el ingreso).
                RefundId = e.OperatorRefundReceivedId
                    ?? (e.ManualCashMovement != null ? e.ManualCashMovement.OperatorRefundReceivedId : null),
                e.Amount,
            })
            .Where(x => x.RefundId != null && refundIds.Contains(x.RefundId.Value))
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.RefundId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
    }
}
