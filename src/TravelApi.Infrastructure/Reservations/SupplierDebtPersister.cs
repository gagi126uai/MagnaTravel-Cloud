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
///
/// <para><b>(2026-07-15) Ya no es SOLO caja</b>: ademas de compras confirmadas - pagos, el saldo oficial suma
/// los cargos del operador facturados APARTE (<see cref="OperatorChargeInvoicedReader"/>, ADR-044 T2 Addendum)
/// — deuda real hacia el operador que antes solo se veia en el extracto (bloque "Circuito de cancelacion") pero
/// nunca en <c>Supplier.CurrentBalance</c>/<c>SupplierBalanceByCurrency</c> (gap admitido y cerrado en esta
/// fecha). La multa RETENIDA de un reembolso (<c>PenaltyRetained</c>) y el reembolso recibido
/// (<c>RefundReceived</c>) siguen SIN sumar aca a proposito: ya estan neteados en el reembolso esperado del
/// operador (<c>BookingCancellationLine.RefundCap</c>), sumarlos tambien aca los contaria dos veces.</para>
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

        // (2026-06-26) El modo de facturacion del proveedor decide si sus servicios generan deuda de compra.
        // Un proveedor CommissionOnly (intermediacion) factura directo al cliente: la agencia NO le compra, asi
        // que sus costos confirmados NO son Cuenta por Pagar. Se pasa el modo al calculo para que la deuda
        // materializada (escalar + tabla hija) lo respete. Ver SupplierDebtCalculator.SupplierGeneratesPurchaseDebt.
        var porMoneda = await CalculateDebtByCurrencyAsync(db, supplierId, supplier.InvoicingMode, ct);

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
                row.OperatorChargesInvoiced = line.OperatorChargesInvoiced;
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
                    OperatorChargesInvoiced = line.OperatorChargesInvoiced,
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
    /// (2026-06-26) Recalcula y persiste la deuda de TODOS los operadores que participan de una reserva, en la
    /// MISMA transaccion del caller. Junta los <c>SupplierId</c> distintos de los 6 tipos de servicio de la
    /// reserva (<see cref="GetReservaSupplierIdsAsync"/>), corre <see cref="PersistAsync"/> por cada uno y hace
    /// UN solo <c>SaveChanges</c> al final (PersistAsync no guarda por si mismo).
    ///
    /// <para><b>Por que existe</b>: este patron (recalcular la deuda de cada operador tras cancelar los servicios
    /// de una reserva) lo necesitan DOS caminos: la anulacion total formal con Nota de Credito
    /// (<c>BookingCancellationService.RecalculateMoneyAfterTotalCancellationAsync</c>) y el caso (3) del flujo
    /// "Anular reserva" (anular en firme sin factura pero con cobros -> saldo a favor,
    /// <c>ReservaService.ApplyAnnulWithPaymentsToCreditAsync</c>). Sin esto, este ultimo dejaba la deuda del
    /// operador INFLADA (seguia contando servicios ya anulados). Centralizar la regla evita una tercera copia que
    /// pueda divergir.</para>
    ///
    /// <para><b>Precondicion de orden</b>: el caller ya debe haber persistido (SaveChanges) la cancelacion de los
    /// servicios y/o el cambio de estado de la reserva ANTES de llamar aca, porque este metodo LEE de la base
    /// (AsNoTracking) para recalcular. Si corre antes del flush, los servicios cancelados todavia contarian.</para>
    /// </summary>
    public static async Task PersistForReservaSuppliersAsync(
        AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var affectedSupplierIds = await GetReservaSupplierIdsAsync(db, reservaId, ct);
        foreach (var supplierId in affectedSupplierIds)
        {
            await PersistAsync(db, supplierId, ct);
        }

        // PersistAsync no guarda solo -> persistimos la deuda de todos los proveedores de una vez. Si no hay
        // proveedores, no hay nada que guardar (evitamos un SaveChanges al pedo).
        if (affectedSupplierIds.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// (2026-06-26) Junta los <c>SupplierId</c> DISTINTOS de los 6 tipos de servicio de una reserva (los 5
    /// tipados + el generico). El generico tiene <c>SupplierId</c> nullable -> se filtran los NULL. Es el mismo
    /// universo que <c>SupplierService.BuildSupplierServicesQuery</c>. Devuelve la lista para que el caller la
    /// reuse (recalcular deuda, armar lineas de cancelacion por operador, etc.).
    /// </summary>
    public static async Task<List<int>> GetReservaSupplierIdsAsync(
        AppDbContext db, int reservaId, CancellationToken ct = default)
    {
        var supplierIds = new HashSet<int>();

        // Fuente 1: tabla generica. SupplierId es nullable -> filtramos los NULL.
        var genericSupplierIds = await db.Servicios
            .Where(s => s.ReservaId == reservaId && s.SupplierId != null)
            .Select(s => s.SupplierId!.Value)
            .Distinct()
            .ToListAsync(ct);
        foreach (var id in genericSupplierIds)
            supplierIds.Add(id);

        // Fuentes 2-6: tablas tipadas. SupplierId es NOT NULL en todas.
        var hotelSupplierIds = await db.HotelBookings
            .Where(h => h.ReservaId == reservaId)
            .Select(h => h.SupplierId).Distinct().ToListAsync(ct);
        foreach (var id in hotelSupplierIds)
            supplierIds.Add(id);

        var flightSupplierIds = await db.FlightSegments
            .Where(f => f.ReservaId == reservaId)
            .Select(f => f.SupplierId).Distinct().ToListAsync(ct);
        foreach (var id in flightSupplierIds)
            supplierIds.Add(id);

        var transferSupplierIds = await db.TransferBookings
            .Where(t => t.ReservaId == reservaId)
            .Select(t => t.SupplierId).Distinct().ToListAsync(ct);
        foreach (var id in transferSupplierIds)
            supplierIds.Add(id);

        var packageSupplierIds = await db.PackageBookings
            .Where(p => p.ReservaId == reservaId)
            .Select(p => p.SupplierId).Distinct().ToListAsync(ct);
        foreach (var id in packageSupplierIds)
            supplierIds.Add(id);

        var assistanceSupplierIds = await db.AssistanceBookings
            .Where(a => a.ReservaId == reservaId)
            .Select(a => a.SupplierId).Distinct().ToListAsync(ct);
        foreach (var id in assistanceSupplierIds)
            supplierIds.Add(id);

        return supplierIds.ToList();
    }

    /// <summary>
    /// Deuda por moneda = compras CONFIRMADAS (por la regla oficial por tipo) menos pagos vivos imputados.
    /// Recorre los 5 tipos tipados + el servicio generico, exactamente el mismo universo que
    /// <c>SupplierService.BuildSupplierServicesQuery</c>. La regla por tipo
    /// (<c>WorkflowStatusHelper.CountsForSupplierDebtByType</c>) no se traduce a SQL, por eso se materializa
    /// (volumen chico por proveedor) y se filtra en memoria.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, SupplierDebtLine>> CalculateDebtByCurrencyAsync(
        AppDbContext db, int supplierId, SupplierInvoicingMode invoicingMode, CancellationToken ct)
    {
        // (2026-06-26) Solo el reseller (TotalToCustomer) genera deuda de compra. Para un CommissionOnly la deuda
        // de costo es CERO: no construimos compras confirmadas (los pagos se siguen procesando mas abajo). Asi la
        // deuda materializada no infla las Cuentas por Pagar con costos que la agencia nunca le debe al operador.
        var generatesPurchaseDebt = SupplierDebtCalculator.SupplierGeneratesPurchaseDebt(invoicingMode);

        var rows = generatesPurchaseDebt
            ? await BuildSupplierServiceDebtRowsAsync(db, supplierId, ct)
            : new List<SupplierDebtServiceRow>();

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

        // (2026-07-15) Cargos del operador facturados APARTE con su propio documento: deuda nueva hacia el
        // operador, independiente del modo de facturacion (un CommissionOnly no deberia tener uno de estos
        // cargos por construccion — el servicio los bloquea al cargarlos, ver
        // BookingCancellationService.AnyLineHasCommissionOnlyInvoicingMode — pero si existiera, es deuda real
        // y debe sumar igual). Mismo lector que usa el desglose por reserva, asi el saldo OFICIAL coincide con
        // lo que el extracto del operador ya muestra en el bloque "Circuito de cancelacion".
        var invoicedChargeRows = await OperatorChargeInvoicedReader.LoadAsync(db, supplierId, ct);
        var operatorChargesInvoiced = invoicedChargeRows
            .Select(r => new SupplierDebtCalculator.ConfirmedPurchase(r.Currency, r.Amount));

        return SupplierDebtCalculator.Calculate(confirmedPurchases, payments, operatorChargesInvoiced);
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
