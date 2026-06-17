using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-022 §4.5: reversa (contra-asiento) del asiento de caja vigente de un COBRO cuando ese cobro se
/// anula/borra. Punto UNICO para que TODOS los caminos de baja de un cobro escriban la reversa de la misma
/// forma — antes vivia solo dentro de <c>PaymentService</c> (camino canonico de /api/payments) y el camino
/// legacy anidado (<c>ReservaService.DeletePaymentAsync</c>, DELETE /api/reservas/{id}/payments/{pid}) NO la
/// escribia, dejando el Libro de Caja inflado (caja descuadrada) al borrar por ahi un cobro que movio caja.
///
/// <para><b>Sin estado, sobre el AppDbContext del caller</b> (mismo patron que <see cref="OverpaymentCreditCleanup"/>
/// y <see cref="ReservaMoneyPersister"/>): NO hace <c>SaveChanges</c>. La marca del asiento viejo y el alta de la
/// reversa quedan en la MISMA transaccion que el soft-delete del cobro, para que el cobro y su contra-asiento se
/// confirmen o se caigan juntos (atomicidad).</para>
/// </summary>
public static class CashLedgerPaymentReversal
{
    /// <summary>
    /// ADR-022 §4.5: marca el asiento vigente del cobro como revertido e inserta su reversa, en el ORDEN
    /// estricto que exige el indice unico parcial (marcar el viejo <c>IsReversed=true</c> ANTES de hacer
    /// <c>Add</c> de la reversa). NO hace <c>SaveChanges</c> — lo hace el caller dentro de su transaccion.
    ///
    /// <para>Solo lo debe llamar el caller cuando el cobro <c>AffectsCash</c> (un puente/saldo a favor no
    /// movio caja y no tiene asiento). Si el cobro no tiene asiento vigente (legacy sin backfill todavia),
    /// no hace nada — mismo no-op tolerante que el camino canonico.</para>
    /// </summary>
    public static async Task ReverseLivePaymentEntryAsync(
        AppDbContext db,
        int paymentId,
        string? actorUserId,
        string? actorUserName,
        CancellationToken ct = default)
    {
        var live = await db.CashLedgerEntries
            .FirstOrDefaultAsync(
                e => e.PaymentId == paymentId && !e.IsReversal && !e.IsReversed,
                ct);
        if (live is null) return;

        // 1) sacar el viejo del indice de vigentes ANTES de insertar nada nuevo.
        live.IsReversed = true;
        // 2) insertar la reversa (Direction invertida, ReversedEntryId al viejo).
        var reversal = CashLedgerEntryFactory.Reverse(live, DateTime.UtcNow, actorUserId, actorUserName);
        db.CashLedgerEntries.Add(reversal);
    }
}
