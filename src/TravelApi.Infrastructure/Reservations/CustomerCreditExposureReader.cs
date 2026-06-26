using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-040 (cuenta corriente del cliente, 2026-06-26): lee la EXPOSICION DE CREDITO de un cliente por moneda —
/// cuanto te debe en total contando TODAS sus reservas en firme, INCLUIDAS las que ya estan "En viaje".
///
/// <para><b>Por que NO reusa <c>FinancePositionService.GetCustomerReceivableByCurrencyAsync</c></b> (review B1,
/// leccion ADR-033 "no mezclar sets de estados"): ese metodo es el AR de COBRANZA y usa
/// <c>ReceivableDebtStatuses</c>, que EXCLUYE Traveling a proposito (en prepago una Traveling con deuda "no
/// deberia existir"). La cuenta corriente rompe justo ese supuesto: un cliente a cuenta SI viaja debiendo, y esa
/// deuda en viaje DEBE contar contra su limite. Por eso usa el set DEDICADO
/// <see cref="EstadoReserva.CreditExposureStatuses"/> (que incluye Traveling). Mezclar los dos conceptos en un
/// solo metodo subestimaria la exposicion justo para los clientes que la feature habilita — un agujero de plata.</para>
///
/// <para>Es la misma proyeccion materializada (<see cref="ReservaMoneyByCurrency"/>) que el resto del modulo de
/// plata; solo cambia el filtro de estados. Join explicito contra Reservas (no nav implicita) para correr igual
/// en Postgres e InMemory. Solo saldos positivos (lo que el cliente debe).</para>
/// </summary>
public static class CustomerCreditExposureReader
{
    /// <summary>
    /// Exposicion de credito de UN cliente: moneda normalizada -&gt; deuda viva. Se usa en el gate manual y en el
    /// re-chequeo de concurrencia del job (B2), donde necesitamos el numero FRESCO al momento de aplicar.
    /// </summary>
    public static async Task<Dictionary<string, decimal>> GetExposureByCurrencyAsync(
        AppDbContext db,
        int customerId,
        CancellationToken ct)
    {
        var rows = await (
            from money in db.ReservaMoneyByCurrency
            join reserva in db.Reservas on money.ReservaId equals reserva.Id
            where reserva.PayerId == customerId
                && EstadoReserva.CreditExposureStatuses.Contains(reserva.Status)
                && money.Balance > 0
            group money.Balance by money.Currency into g
            select new { Currency = g.Key, Amount = g.Sum() })
            .ToListAsync(ct);

        return ToNormalizedDictionary(rows.Select(x => (x.Currency, x.Amount)));
    }

    /// <summary>
    /// Exposicion de credito de VARIOS clientes en una sola pasada (planificacion del job, anti N+1):
    /// customerId -&gt; (moneda normalizada -&gt; deuda viva). Clientes sin deuda no aparecen en el diccionario.
    /// </summary>
    public static async Task<Dictionary<int, Dictionary<string, decimal>>> GetExposureByCurrencyForCustomersAsync(
        AppDbContext db,
        IReadOnlyCollection<int> customerIds,
        CancellationToken ct)
    {
        if (customerIds.Count == 0)
        {
            return new Dictionary<int, Dictionary<string, decimal>>();
        }

        var rows = await (
            from money in db.ReservaMoneyByCurrency
            join reserva in db.Reservas on money.ReservaId equals reserva.Id
            where reserva.PayerId != null
                && customerIds.Contains(reserva.PayerId.Value)
                && EstadoReserva.CreditExposureStatuses.Contains(reserva.Status)
                && money.Balance > 0
            group money.Balance by new { CustomerId = reserva.PayerId!.Value, money.Currency } into g
            select new { g.Key.CustomerId, g.Key.Currency, Amount = g.Sum() })
            .ToListAsync(ct);

        var result = new Dictionary<int, Dictionary<string, decimal>>();
        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.CustomerId, out var byCurrency))
            {
                byCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);
                result[row.CustomerId] = byCurrency;
            }

            var key = Monedas.Normalizar(row.Currency);
            byCurrency[key] = byCurrency.TryGetValue(key, out var current) ? current + row.Amount : row.Amount;
        }

        return result;
    }

    /// <summary>
    /// Normaliza la moneda (null/vacio -&gt; ARS para servicios genericos legacy) agrupando en memoria. Mismo
    /// criterio que <c>FinancePositionService.Normalize</c>.
    /// </summary>
    private static Dictionary<string, decimal> ToNormalizedDictionary(IEnumerable<(string? Currency, decimal Amount)> rows)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (currency, amount) in rows)
        {
            var key = Monedas.Normalizar(currency);
            totals[key] = totals.TryGetValue(key, out var current) ? current + amount : amount;
        }

        return totals;
    }
}
