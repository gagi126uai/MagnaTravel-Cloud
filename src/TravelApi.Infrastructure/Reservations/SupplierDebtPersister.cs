using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-022 §4.10 (fix P1, 2026-06-11): UNICO punto de escritura de la deuda de un proveedor, SIN estado.
/// Espejo de <see cref="ReservaMoneyPersister"/> del lado cliente: recalcula la deuda por moneda con
/// <see cref="SupplierDebtCalculator"/> y persiste, en la MISMA transaccion del caller:
/// <list type="number">
/// <item>el escalar surrogate <c>Supplier.CurrentBalance</c> (semaforo §15.3); y</item>
/// <item>la tabla hija <c>SupplierBalanceByCurrency</c> (upsert por moneda, borrar las ausentes).</item>
/// </list>
///
/// <para><b>Por que existe</b>: antes esta logica vivia SOLO dentro de <c>SupplierService</c>. El servicio
/// GENERICO (<c>ReservaService.Add/Update/RemoveServiceAsync</c>) tocaba el saldo de la RESERVA pero nunca
/// recalculaba la deuda del proveedor, asi que <c>Supplier.CurrentBalance</c> y
/// <c>SupplierBalanceByCurrency</c> quedaban STALE (bug P1 del ADR-022). Inyectar <c>ISupplierService</c> en
/// <c>ReservaService</c> arriesgaba un ciclo de dependencias; en cambio, este helper sin estado opera sobre
/// el <c>AppDbContext</c> que el caller ya tiene, igual que <see cref="ReservaMoneyPersister"/>.</para>
///
/// <para><b>Fuente unica de la cuenta</b>: <c>SupplierService.PersistSupplierBalanceAsync</c> delega aca,
/// asi que el numero que produce este persister es EXACTAMENTE el mismo que daria el servicio del proveedor
/// (no hay dos formulas que puedan discrepar). El calculo en si sigue siendo puro en
/// <see cref="SupplierDebtCalculator"/>; este helper solo materializa la query y escribe la proyeccion.</para>
/// </summary>
internal static class SupplierDebtPersister
{
    // ADR-022 §4.10 (fix #4): estados de Reserva "vivos" para la cuenta del proveedor. FUENTE UNICA en
    // Domain (SupplierDebtCalculator.ValidReservationStatuses), consumida tambien por SupplierService: el
    // numero de deuda tiene que ser identico salga del servicio del proveedor o de este persister, asi que
    // NO se vuelve a declarar la lista (antes estaba duplicada y podia divergir en silencio).
    private static readonly string[] ValidReservationStatuses = SupplierDebtCalculator.ValidReservationStatuses;

    /// <summary>
    /// Recalcula y persiste la deuda del proveedor indicado (escalar + tabla hija). NO llama a
    /// SaveChanges: lo hace el caller, asi escalar y tabla hija quedan en la misma transaccion. No hace
    /// nada si el proveedor no existe (mismo comportamiento que <c>SupplierService.UpdateBalanceAsync</c>).
    /// </summary>
    public static async Task PersistAsync(AppDbContext db, int supplierId, CancellationToken ct = default)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, ct);
        if (supplier == null) return;

        var porMoneda = await CalculateDebtByCurrencyAsync(db, supplierId, ct);

        // 1) Escalar surrogate (semaforo): identico a la cuenta legacy mono-moneda.
        supplier.CurrentBalance = SupplierDebtCalculator.ToSurrogateBalance(porMoneda);

        // 2) Tabla hija: upsert por moneda + borrar las monedas que ya no aplican.
        var existingRows = await db.SupplierBalanceByCurrency
            .Where(row => row.SupplierId == supplierId)
            .ToListAsync(ct);
        var existingByCurrency = existingRows.ToDictionary(row => row.Currency, StringComparer.Ordinal);

        foreach (var (currency, line) in porMoneda)
        {
            if (existingByCurrency.TryGetValue(currency, out var row))
            {
                row.ConfirmedPurchases = line.ConfirmedPurchases;
                row.TotalPaid = line.TotalPaid;
                row.Balance = line.Balance;
            }
            else
            {
                db.SupplierBalanceByCurrency.Add(new SupplierBalanceByCurrency
                {
                    SupplierId = supplierId,
                    Currency = currency,
                    ConfirmedPurchases = line.ConfirmedPurchases,
                    TotalPaid = line.TotalPaid,
                    Balance = line.Balance
                });
            }
        }

        foreach (var row in existingRows)
        {
            if (!porMoneda.ContainsKey(row.Currency))
                db.SupplierBalanceByCurrency.Remove(row);
        }
    }

    /// <summary>
    /// Deuda por moneda = compras CONFIRMADAS (por la regla oficial por tipo) menos pagos vivos imputados.
    /// Recorre los 5 tipos tipados + el servicio generico, exactamente el mismo universo que
    /// <c>SupplierService.BuildSupplierServicesQuery</c>. La regla por tipo
    /// (<c>WorkflowStatusHelper.CountsForSupplierDebtByType</c>) no se traduce a SQL, por eso se materializa
    /// (volumen chico por proveedor) y se filtra en memoria.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, SupplierDebtLine>> CalculateDebtByCurrencyAsync(
        AppDbContext db, int supplierId, CancellationToken ct)
    {
        var rows = await BuildSupplierServiceDebtRowsAsync(db, supplierId, ct);

        var confirmedPurchases = rows
            .Where(r => WorkflowStatusHelper.CountsForSupplierDebtByType(r.Type, r.Status))
            .Select(r => new SupplierDebtCalculator.ConfirmedPurchase(r.Currency, r.NetCost));

        // El query filter !IsDeleted (AppDbContext) excluye los pagos soft-deleted, por eso una anulacion
        // es self-healing: el pago borrado deja de sumar y la deuda de su moneda vuelve a subir.
        var paymentRows = await db.SupplierPayments
            .Where(payment => payment.SupplierId == supplierId)
            .Select(payment => new
            {
                payment.Amount,
                payment.Currency,
                payment.ImputedCurrency,
                payment.ImputedAmount
            })
            .ToListAsync(ct);

        var payments = paymentRows.Select(p => new SupplierDebtCalculator.SupplierPaymentInput(
            p.Amount, p.Currency, p.ImputedCurrency, p.ImputedAmount));

        return SupplierDebtCalculator.Calculate(confirmedPurchases, payments);
    }

    /// <summary>
    /// Reune (Type, Status, NetCost, Currency) de cada servicio del proveedor en reservas vivas. Recorre los
    /// 5 tipos tipados + el generico (mismo universo que <c>SupplierService.BuildSupplierServicesQuery</c>).
    /// Cada tipo se materializa por separado y se combina en memoria: NO se usa <c>IQueryable.Concat</c>
    /// porque el provider InMemory no lo traduce sobre proyecciones, y el volumen por proveedor es chico.
    /// </summary>
    private static async Task<List<SupplierDebtServiceRow>> BuildSupplierServiceDebtRowsAsync(
        AppDbContext db, int supplierId, CancellationToken ct)
    {
        var rows = new List<SupplierDebtServiceRow>();

        var flights = await db.FlightSegments.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && ValidReservationStatuses.Contains(s.Reserva!.Status))
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        rows.AddRange(flights.Select(s => new SupplierDebtServiceRow("Vuelo", s.Status, s.NetCost, s.Currency)));

        var hotels = await db.HotelBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && ValidReservationStatuses.Contains(s.Reserva!.Status))
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        rows.AddRange(hotels.Select(s => new SupplierDebtServiceRow("Hotel", s.Status, s.NetCost, s.Currency)));

        var transfers = await db.TransferBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && ValidReservationStatuses.Contains(s.Reserva!.Status))
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        rows.AddRange(transfers.Select(s => new SupplierDebtServiceRow("Traslado", s.Status, s.NetCost, s.Currency)));

        var packages = await db.PackageBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && ValidReservationStatuses.Contains(s.Reserva!.Status))
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        rows.AddRange(packages.Select(s => new SupplierDebtServiceRow("Paquete", s.Status, s.NetCost, s.Currency)));

        var assistances = await db.AssistanceBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && ValidReservationStatuses.Contains(s.Reserva!.Status))
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        rows.AddRange(assistances.Select(s => new SupplierDebtServiceRow("Asistencia", s.Status, s.NetCost, s.Currency)));

        // El generico aporta su propio ServiceType como Type (igual que BuildSupplierServicesQuery), para
        // que la regla por tipo lo clasifique por su texto de estado.
        var services = await db.Servicios.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && ValidReservationStatuses.Contains(s.Reserva!.Status))
            .Select(s => new { s.ServiceType, s.Status, s.NetCost, s.Currency }).ToListAsync(ct);
        rows.AddRange(services.Select(s => new SupplierDebtServiceRow(s.ServiceType, s.Status, s.NetCost, s.Currency)));

        return rows;
    }

    /// <summary>Tupla minima (tipo/estado/costo/moneda) para clasificar y sumar la deuda por moneda.</summary>
    private readonly record struct SupplierDebtServiceRow(string Type, string Status, decimal NetCost, string Currency);
}
