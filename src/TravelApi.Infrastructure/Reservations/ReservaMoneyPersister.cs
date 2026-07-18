using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-021 §4.1/§B5 (multimoneda, 2026-06-08): UNICO punto de escritura de la plata de una Reserva.
/// Recalcula con <see cref="ReservaMoneyCalculator"/> y persiste, en la MISMA <c>SaveChangesAsync</c>:
/// <list type="number">
/// <item>los 5 escalares de la <c>Reserva</c> (<c>TotalSale/ConfirmedSale/TotalCost/TotalPaid/Balance</c>,
///   con <c>Balance</c> = surrogate/semaforo §2.4); y</item>
/// <item>las filas de la tabla hija <c>ReservaMoneyByCurrency</c> (upsert por moneda, borrando las
///   monedas que ya no aplican).</item>
/// </list>
///
/// <para><b>Por que existe</b> (bloqueante B5 del review): antes habia TRES rutinas byte-identicas que
/// recalculaban y escribian el escalar (<c>ReservaService.RecalculateMoneyAsync</c>,
/// <c>PaymentService.RecalculateReservaBalanceAsync</c> y <c>AfipService.RecalculateReservaBalanceAsync</c>).
/// Si solo dos de ellas sincronizaban la tabla hija, el path que no lo hacia (la reversa de NC en
/// AfipService) dejaba la hija con saldo viejo -> cuenta corriente / reportes / top-N mostraban datos
/// desactualizados sin romper nada (el peor bug: miente en silencio). Consolidando las tres aca,
/// escalar y hija se escriben por UN solo camino y nunca pueden divergir.</para>
///
/// <para><b>Atomicidad</b>: escalar y filas hijas se graban en la misma <c>SaveChangesAsync</c>. Si
/// falla, no se persiste ninguno de los dos (la transaccion del caller, si la hay, hace el resto).</para>
///
/// <para><b>Es una proyeccion, no una segunda fuente de verdad</b>: la unica logica de calculo sigue
/// viviendo en <c>ReservaMoneyCalculator</c>; la tabla hija es su materializado, reescrito en cada
/// recalculo. Por eso el backfill (Capa 2) reusa este mismo persister.</para>
///
/// <para><b>ADR-048 (2026-07-17)</b>: este metodo es tambien el chokepoint elegido para la via ATOMICA
/// del estado derivado de la reserva (B2): antes de su <c>SaveChangesAsync</c> invoca
/// <see cref="ReservaTerminalTransitionApplier"/>, que lleva la reserva al terminal del par (Anulada /
/// Esperando reembolso del operador) cuando corresponde. Asi el cambio de estado nunca queda en un
/// commit separado del cambio de saldo — comparten la MISMA transaccion.</para>
///
/// <para><b>ADR-048 T5 (2026-07-17, hardening)</b>: en la MISMA pasada tambien materializa los DOS ejes
/// secundarios (<c>Reserva.DerivedCollectionStatus</c> / <c>Reserva.DerivedInvoicingStatus</c>) via
/// <see cref="ReservaDerivedAxesProjector"/> — mismo commit, mismo chokepoint, unico escritor.</para>
/// </summary>
internal static class ReservaMoneyPersister
{
    /// <summary>
    /// Carga la reserva con todos los Includes economicos, recalcula y persiste escalar + tabla hija.
    /// No hace nada si la reserva no existe (mismo comportamiento que las rutinas que reemplaza).
    /// </summary>
    /// <param name="allowTerminalCorrectionWithinPar">
    /// Default <c>false</c> (comportamiento de siempre). Ver el mismo parametro en
    /// <see cref="ReservaTerminalTransitionApplier.ApplyIfNeededAsync"/>: solo hay que pasarlo en
    /// <c>true</c> desde el UNICO caller que lo necesita (el fix de B-1 en
    /// <c>BookingCancellationService.CancelServiceAsync</c>, paso 6). El resto de los callers de este
    /// persister (pagos, AFIP, anulacion total, "anular con saldo a favor") deben dejarlo en <c>false</c>:
    /// esos flujos ya deciden el cierre del par por su cuenta con reglas mas completas (multa/ND a medio
    /// emitir, sincronizacion con BookingCancellation.Status, receivable a nivel proveedor) que este
    /// persister no conoce — pasar <c>true</c> ahi les pisaria esa decision.
    /// </param>
    public static async Task PersistAsync(
        AppDbContext db, int reservaId, CancellationToken ct = default,
        bool allowTerminalCorrectionWithinPar = false)
    {
        // Grafo economico completo: pagos + 5 tipados + generico. Centralizado aca para que las tres
        // rutinas que delegan en este persister carguen exactamente el mismo grafo (antes cada una
        // mantenia su propia lista de Includes y podian desincronizarse al agregar un tipo de servicio).
        //
        // ADR-048 T5: se agrega Include(Invoices) — antes no hacia falta aca (la plata de la reserva no
        // depende de los comprobantes), pero el eje de facturacion materializado (DerivedInvoicingStatus)
        // SI necesita los comprobantes para calcular el cuadre (ver ReservaDerivedAxesProjector).
        var reserva = await db.Reservas
            .Include(f => f.Payments)
            .Include(f => f.Servicios)
            .Include(f => f.FlightSegments)
            .Include(f => f.HotelBookings)
            .Include(f => f.TransferBookings)
            .Include(f => f.PackageBookings)
            .Include(f => f.AssistanceBookings)
            .Include(f => f.Invoices)
            .FirstOrDefaultAsync(f => f.Id == reservaId, ct);

        if (reserva == null) return;

        var summary = ReservaMoneyCalculator.Calculate(reserva);

        // 1) Escalares de compat (Balance = surrogate §2.4). Misma asignacion byte-identica que las
        //    tres rutinas viejas; el calculator es la fuente unica de la cuenta.
        reserva.TotalSale = summary.TotalSale;
        reserva.ConfirmedSale = summary.ConfirmedSale;
        reserva.TotalCost = summary.TotalCost;
        reserva.TotalPaid = summary.TotalPaid;
        reserva.Balance = summary.Balance;

        // 2) Tabla hija: upsert por moneda + borrar las monedas que ya no aplican.
        await SyncMoneyByCurrencyRowsAsync(db, reservaId, summary, ct);

        // 2-ter) ADR-048 T5 (2026-07-17, hardening): materializa los dos ejes secundarios (cobro /
        //    facturacion) en la cabecera de la reserva, en la MISMA pasada — mismo commit que el escalar
        //    y la tabla hija de arriba. Se recalculan SIEMPRE, sin mirar Status: por eso no hace falta un
        //    caso especial para el par {Cancelled, PendingOperatorRefund} (B3, ver el XML-doc del
        //    proyector). "reserva.Invoices" ya viene cargado por el Include de arriba.
        reserva.DerivedCollectionStatus = ReservaDerivedAxesProjector.ProjectCollectionStatus(summary);
        reserva.DerivedInvoicingStatus = ReservaDerivedAxesProjector.ProjectInvoicingStatus(summary.TotalSale, reserva.Invoices);

        // 2-bis) ADR-048 (2026-07-17, modelo de estados derivados, via atomica B2): si la reserva "tuvo
        //    servicios y los tiene todos anulados" (INV-048-01), esto la lleva al terminal del par
        //    (Esperando reembolso del operador / Anulada, segun corresponda a nivel reserva con N
        //    cancelaciones — B1) ANTES del SaveChanges de la plata. Asi estado y saldo quedan en el MISMO
        //    commit: no existe una ventana donde el saldo ya se actualizo pero el cartel de la reserva
        //    sigue mintiendo (regla 9, nada de "la proxima mutacion lo corrige"). Es un no-op si la
        //    reserva no aplica (esta viva, o ya esta en el terminal correcto).
        await ReservaTerminalTransitionApplier.ApplyIfNeededAsync(
            db, reserva, DateTime.UtcNow, ct, allowCorrectionWithinPar: allowTerminalCorrectionWithinPar);

        // 3) Escalar, filas hijas y (si aplico el punto anterior) el terminal derivado, todos en UNA sola
        //    SaveChangesAsync -> nunca divergen.
        await db.SaveChangesAsync(ct);

        // 4) Comision del vendedor (auditoria ERP 2026-06-12, hallazgo #1): este es el chokepoint de la
        //    plata de la reserva, asi que devengar/revertir la comision aca cubre TODOS los caminos que
        //    mueven el saldo (cobro, mutacion de servicio, anulacion de factura, cancelacion) sin tocar
        //    cada call-site. Corre DESPUES del SaveChanges del saldo para leer un Balance ya actualizado;
        //    guarda sus propios cambios en una SaveChanges separada. Si el toggle EnableSellerCommissions
        //    esta apagado, el persister es un no-op total (comportamiento byte-identico a antes).
        await CommissionAccrualPersister.RecalculateAsync(db, reservaId, ct);
    }

    /// <summary>
    /// ADR-048 T5 fix B1 (2026-07-17, review backend): refresca SOLO <c>Reserva.DerivedInvoicingStatus</c>
    /// — sin tocar cobro, servicios ni pagos. Existe para los caminos que cambian un COMPROBANTE (factura
    /// de venta o Nota de Debito con CAE aprobado) pero que NUNCA mueven el saldo de la reserva (ADR-037:
    /// facturar esta desacoplado del cobro), y por eso normalmente NO pasan por <see cref="PersistAsync"/>.
    ///
    /// <para><b>El bug que esto cierra</b>: antes de este fix, el UNICO camino que refrescaba
    /// <c>DerivedInvoicingStatus</c> era <see cref="PersistAsync"/> (llamado, para comprobantes, SOLO
    /// cuando se aprueba una Nota de Credito — <c>AfipService.ApplyCreditNoteEconomicReversalAsync</c>).
    /// Emitir una FACTURA DE VENTA o una ND nunca pasaba por ahi, asi que la columna quedaba con el valor
    /// del ULTIMO movimiento de plata (tipicamente "NotInvoiced") para siempre — el listado (que ahora lee
    /// la columna) mentia "Sin facturar" mientras el detalle (que sigue derivando en vivo) decia
    /// "Facturada total". Ver <c>docs/architecture/2026-07-17-t5-review-backend.md</c> §B1.</para>
    ///
    /// <para><b>Por que NO recalcular todo (por que no llamar <see cref="PersistAsync"/> aca en su
    /// lugar)</b>: emitir/anular un comprobante NO cambia <c>TotalSale</c> (que sale de los SERVICIOS, no
    /// de los comprobantes) ni el saldo del cliente en el circuito normal (eso lo mueve un Payment, no una
    /// Invoice) — por eso el proyector puede usar el <c>TotalSale</c> YA PERSISTIDO tal cual, sin
    /// recorrer de nuevo las 6 colecciones de servicios ni los pagos. Recalcular todo en CADA emision de
    /// factura (el camino MAS FRECUENTE de mutacion de comprobantes) seria carga de escritura innecesaria
    /// en el camino caliente. Si algun dia una emision SI llegara a mover plata (hoy no pasa en ningun
    /// flujo conocido), ese camino debe seguir usando <see cref="PersistAsync"/> completo, no este atajo.</para>
    ///
    /// <para><b>Para Notas de Credito seguí usando <see cref="PersistAsync"/> completo</b> (via
    /// <c>AfipService.ApplyCreditNoteEconomicReversalAsync</c>): una NC SI mueve plata (genera la reversion
    /// economica del pago), asi que necesita el recalculo completo de cobro + facturacion, no este atajo.</para>
    /// </summary>
    public static async Task RefreshInvoicingAxisOnlyAsync(AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var reserva = await db.Reservas
            .Include(f => f.Invoices)
            .FirstOrDefaultAsync(f => f.Id == reservaId, ct);

        if (reserva == null) return;

        reserva.DerivedInvoicingStatus = ReservaDerivedAxesProjector.ProjectInvoicingStatus(reserva.TotalSale, reserva.Invoices);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Sincroniza las filas de <c>ReservaMoneyByCurrency</c> con el detalle por moneda del summary.
    /// Carga las filas actuales de la reserva (a lo sumo dos), las actualiza/crea segun
    /// <c>summary.PorMoneda</c> y borra las que ya no tienen presencia (ej. se cancelo el unico
    /// servicio USD). NO llama a SaveChanges: lo hace el caller junto con el escalar.
    /// </summary>
    private static async Task SyncMoneyByCurrencyRowsAsync(
        AppDbContext db, int reservaId, ReservaMoneySummary summary, CancellationToken ct)
    {
        var existingRows = await db.ReservaMoneyByCurrency
            .Where(row => row.ReservaId == reservaId)
            .ToListAsync(ct);

        var existingByCurrency = existingRows.ToDictionary(row => row.Currency, StringComparer.Ordinal);

        // Upsert: una fila por cada moneda presente en el detalle.
        foreach (var (currency, line) in summary.PorMoneda)
        {
            if (existingByCurrency.TryGetValue(currency, out var row))
            {
                row.TotalSale = line.TotalSale;
                row.ConfirmedSale = line.ConfirmedSale;
                row.TotalCost = line.TotalCost;
                row.TotalPaid = line.TotalPaid;
                row.Balance = line.Balance;
            }
            else
            {
                db.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
                {
                    ReservaId = reservaId,
                    Currency = currency,
                    TotalSale = line.TotalSale,
                    ConfirmedSale = line.ConfirmedSale,
                    TotalCost = line.TotalCost,
                    TotalPaid = line.TotalPaid,
                    Balance = line.Balance
                });
            }
        }

        // Borrar las monedas que ya no aparecen en el detalle (no quedan filas hijas fantasma).
        foreach (var row in existingRows)
        {
            if (!summary.PorMoneda.ContainsKey(row.Currency))
                db.ReservaMoneyByCurrency.Remove(row);
        }
    }
}
