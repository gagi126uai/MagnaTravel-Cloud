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

    // Estados de reserva que cuentan como cuenta por cobrar (AR). Definicion CANONICA y unica:
    // antes tesoreria usaba esta lista positiva y el dashboard una lista negativa (!= Closed/Cancelled/Budget)
    // -> daban numeros distintos para Cotizacion/Perdido. ADR-022 §4.7 fija la lista positiva como la verdad:
    // una cotizacion o un presupuesto todavia NO tienen saldo exigible, asi que no son AR.
    private static readonly string[] ActiveReceivableStatuses =
    {
        EstadoReserva.InManagement,
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
        EstadoReserva.ToSettle
    };

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
            where ActiveReceivableStatuses.Contains(reservaPadre.Status) && row.Balance > 0
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
