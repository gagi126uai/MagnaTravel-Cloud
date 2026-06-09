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
/// </summary>
internal static class ReservaMoneyPersister
{
    /// <summary>
    /// Carga la reserva con todos los Includes economicos, recalcula y persiste escalar + tabla hija.
    /// No hace nada si la reserva no existe (mismo comportamiento que las rutinas que reemplaza).
    /// </summary>
    public static async Task PersistAsync(AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        // Grafo economico completo: pagos + 5 tipados + generico. Centralizado aca para que las tres
        // rutinas que delegan en este persister carguen exactamente el mismo grafo (antes cada una
        // mantenia su propia lista de Includes y podian desincronizarse al agregar un tipo de servicio).
        var reserva = await db.Reservas
            .Include(f => f.Payments)
            .Include(f => f.Servicios)
            .Include(f => f.FlightSegments)
            .Include(f => f.HotelBookings)
            .Include(f => f.TransferBookings)
            .Include(f => f.PackageBookings)
            .Include(f => f.AssistanceBookings)
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

        // 3) Escalar y filas hijas en UNA sola SaveChangesAsync -> nunca divergen.
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
