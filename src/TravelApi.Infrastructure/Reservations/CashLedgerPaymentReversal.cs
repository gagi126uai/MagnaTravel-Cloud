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
    /// <param name="isReplacement">
    /// Hallazgo de review (2026-07-27, bloqueante T-5/backend+security): la firma "par de Caja por
    /// EDICION" es GENERICA — cubre cobros y pagos a proveedor, no solo movimientos manuales. <c>true</c>
    /// cuando este metodo se invoca desde una EDICION de cobro (<c>PaymentService.UpdatePaymentAsync</c>,
    /// <c>ReservaService</c> camino legacy de edicion): el par queda REEMPLAZADO por el asiento nuevo que
    /// el caller inserta a continuacion. <c>false</c> (default) cuando es una ANULACION real (borrar el
    /// cobro): no hay ningun asiento nuevo que lo reemplace. Ver <see cref="CashLedgerEntry.IsReplaced"/>.
    /// </param>
    public static async Task ReverseLivePaymentEntryAsync(
        AppDbContext db,
        int paymentId,
        string? actorUserId,
        string? actorUserName,
        bool isReplacement = false,
        CancellationToken ct = default)
    {
        var live = await db.CashLedgerEntries
            .FirstOrDefaultAsync(
                e => e.PaymentId == paymentId && !e.IsReversal && !e.IsReversed,
                ct);
        if (live is null) return;

        // 1) sacar el viejo del indice de vigentes ANTES de insertar nada nuevo.
        live.IsReversed = true;
        // Hallazgo N1 (review 2026-07-27): mismo criterio que TreasuryService/SupplierService — `live`
        // nace en IsReplaced=false y esta es la primera vez que se revierte; el `if` documenta la
        // intencion en vez de reescribir incondicionalmente con el mismo valor en la anulacion real.
        if (isReplacement) live.IsReplaced = true;
        // 2) insertar la reversa (Direction invertida, ReversedEntryId al viejo).
        var reversal = CashLedgerEntryFactory.Reverse(
            live, DateTime.UtcNow, actorUserId, actorUserName, isReplacement: isReplacement);
        db.CashLedgerEntries.Add(reversal);
    }
}
