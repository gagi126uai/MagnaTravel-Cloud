using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-022 §4.7 (T4): implementacion de la fuente unica AR/AP por moneda. Las dos queries que antes
/// vivian duplicadas en TreasuryService (AR) y ReportService (AP) ahora estan UNA sola vez aca.
/// </summary>
public class FinancePositionService : IFinancePositionService
{
    private readonly AppDbContext _dbContext;

    // Estados de reserva que tienen CUENTA POR COBRAR (deuda viva cobrable). Definicion CANONICA y unica.
    //
    // ADR-033 (2026-06-16, B1 split): esta lista representa el concepto "tiene deuda que se puede cobrar" y
    // ahora INCLUYE Closed (= EstadoReserva.SaleFirmStatuses). Una reserva Finalizada con deuda es una cuenta
    // por cobrar legitima (factura con CAE, saldo que reaparece post-cierre, etc.) y debe verse en AR /
    // cobranza / saldo del cliente / dashboard / alertas. Las queries ya filtran Balance > 0 por moneda, asi
    // que una Closed saldada (Balance 0) NO aparece; solo aparece Closed con deuda.
    //
    // ADR-036 (2026-06-21, prepago puro): SaleFirmStatuses dejo de incluir Traveling. En el modelo prepago una
    // reserva no puede entrar a "En viaje" debiendo (candado duro de pago del cliente), asi que una Traveling
    // con deuda no deberia existir; no se la trata como cuenta por cobrar viva. Quedan {InManagement, Confirmed,
    // Closed}. ToSettle murio.
    //
    // ATENCION (B1): este concepto es DISTINTO de "venta operativa viva" (lead ganado), que sigue usando
    // EstadoReserva.ActiveCollectionStatuses (SIN Closed) en ReservaService.MarkSourceLeadAsWonIfReservaIsFirmAsync.
    // Cerrar una reserva NO debe marcar su lead como Ganado. PROHIBIDO mezclar los dos conceptos en una sola
    // lista (eso era el bug que motivo el split). Por eso esta lista se llama ReceivableDebtStatuses (deuda),
    // no "ActiveReceivableStatuses" (el nombre viejo, que mezclaba ambas intenciones).
    //
    // Historia: antes (ADR-022/023) se llamaba "ActiveReceivableStatuses" y NO incluia Closed -> la deuda en
    // reservas Finalizadas quedaba invisible (el deadlock que ADR-033 resuelve).
    public static readonly string[] ReceivableDebtStatuses = EstadoReserva.SaleFirmStatuses;

    public FinancePositionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<FinanceCurrencyAmount>> GetAccountsReceivableByCurrencyAsync(CancellationToken cancellationToken)
    {
        // Join explicito contra Reservas (no nav implicita) para correr igual en Postgres e InMemory.
        // Solo saldos positivos (lo que el cliente debe), de reservas activas.
        var query =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where ReceivableDebtStatuses.Contains(reservaPadre.Status) && row.Balance > 0
            select new { row.Currency, row.Balance };

        var grouped = await query
            .GroupBy(x => x.Currency)
            .Select(g => new { Currency = g.Key, Amount = g.Sum(x => x.Balance) })
            .ToListAsync(cancellationToken);

        return Normalize(grouped.Select(x => (x.Currency, x.Amount)));
    }

    public async Task<List<FinanceCurrencyAmount>> GetAccountsPayableByCurrencyAsync(CancellationToken cancellationToken)
    {
        // Deuda a proveedores por moneda. Solo saldos positivos (lo que la agencia debe).
        var grouped = await _dbContext.SupplierBalanceByCurrency
            .Where(row => row.Balance > 0)
            .GroupBy(row => row.Currency)
            .Select(g => new { Currency = g.Key, Amount = g.Sum(x => x.Balance) })
            .ToListAsync(cancellationToken);

        return Normalize(grouped.Select(x => (x.Currency, x.Amount)));
    }

    public async Task<List<FinanceCurrencyAmount>> GetCustomerReceivableByCurrencyAsync(int customerId, CancellationToken cancellationToken)
    {
        // ADR-023 T1: misma query que el AR global, acotada a las reservas de ESTE cliente (PayerId).
        // Join explicito contra Reservas (no nav implicita) para correr igual en Postgres e InMemory.
        var query =
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            where reservaPadre.PayerId == customerId
                && ReceivableDebtStatuses.Contains(reservaPadre.Status)
                && row.Balance > 0
            select new { row.Currency, row.Balance };

        var grouped = await query
            .GroupBy(x => x.Currency)
            .Select(g => new { Currency = g.Key, Amount = g.Sum(x => x.Balance) })
            .ToListAsync(cancellationToken);

        return Normalize(grouped.Select(x => (x.Currency, x.Amount)));
    }

    public async Task<decimal> GetCustomerReceivableScalarAsync(int customerId, CancellationToken cancellationToken)
    {
        // El escalar de compat es la suma cross-moneda del desglose por moneda. Es semanticamente impuro
        // (suma ARS + USD) pero es lo que el front actual espera en CurrentBalance / TotalBalance; el desglose
        // real viaja aparte (ReceivableByCurrency). NO se usa para decidir nada por moneda. Igual criterio que
        // los escalares de CashSummaryDto en TreasuryService.
        var byCurrency = await GetCustomerReceivableByCurrencyAsync(customerId, cancellationToken);
        return byCurrency.Sum(x => x.Amount);
    }

    public async Task<Dictionary<Guid, decimal>> GetReceivableScalarByCustomerPublicIdAsync(CancellationToken cancellationToken)
    {
        // ADR-023 T1: una sola pasada para enriquecer/ordenar la lista de clientes. Se agrupa por PublicId
        // porque el DTO de la lista expone PublicId (no el Id interno). El escalar por cliente es la suma
        // cross-moneda de sus saldos en firme (mismo criterio que GetCustomerReceivableScalarAsync, en bloque).
        //
        // Costo: O(clientes con deuda en firme) por request. Para el volumen actual es trivial; si la base de
        // clientes crece mucho, esta agregacion deberia paginarse junto con la lista (no hoy).
        var grouped = await (
            from row in _dbContext.ReservaMoneyByCurrency
            join reservaPadre in _dbContext.Reservas on row.ReservaId equals reservaPadre.Id
            join customer in _dbContext.Customers on reservaPadre.PayerId equals customer.Id
            where ReceivableDebtStatuses.Contains(reservaPadre.Status)
                && row.Balance > 0
            group row.Balance by customer.PublicId into g
            select new { PublicId = g.Key, Amount = g.Sum() })
            .ToListAsync(cancellationToken);

        return grouped.ToDictionary(x => x.PublicId, x => EconomicRulesHelper.RoundCurrency(x.Amount));
    }

    // ADR-033 (B1, C4): helper publico del concepto "tiene cuenta por cobrar" (= ReceivableDebtStatuses,
    // incluye Closed). Hoy NO tiene callers. Si algun consumidor de "venta operativa viva" (lead-won) lo
    // necesitara, NO debe usar este (arrastraria Closed): debe usar EstadoReserva.ActiveCollectionStatuses.
    public bool IsInFirmReceivableStatus(string status)
        => ReceivableDebtStatuses.Contains(status);

    /// <summary>
    /// Normaliza la moneda (null/vacio -> ARS, para servicios genericos legacy) agrupando en memoria,
    /// redondea y ordena por moneda para que el shape sea estable en los tests.
    /// </summary>
    private static List<FinanceCurrencyAmount> Normalize(IEnumerable<(string? Currency, decimal Amount)> rows)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (currency, amount) in rows)
        {
            var key = Monedas.Normalizar(currency);
            totals[key] = totals.TryGetValue(key, out var current) ? current + amount : amount;
        }

        return totals
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new FinanceCurrencyAmount(kvp.Key, EconomicRulesHelper.RoundCurrency(kvp.Value)))
            .ToList();
    }
}
