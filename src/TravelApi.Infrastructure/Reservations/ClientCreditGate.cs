using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-040 (cuenta corriente del cliente, 2026-06-26): puente entre la base de datos y la politica PURA
/// <see cref="ClientCreditPolicy"/>. Resuelve el modo de cobro efectivo del cliente, carga sus limites por
/// moneda y su exposicion FRESCA, y devuelve la decision de credito. Lo usan identico el gate manual
/// (<c>ReservaService.EnsureCanStartTravelingAsync</c>), la planificacion del job y el re-chequeo de
/// concurrencia del apply (review B2) — un solo lugar para no divergir.
///
/// <para><b>Por que la exposicion se relee aca y no se pasa cacheada</b> (review B2): la decision de credito es
/// sobre la deuda TOTAL del cliente, que puede cambiar entre la planificacion y el commit (un cajero cobro/borro
/// un cobro en otra reserva del mismo cliente). El apply re-lee la exposicion FRESCA por moneda y re-evalua la
/// politica completa — NO alcanza con re-chequear el saldo escalar de una sola reserva.</para>
/// </summary>
public static class ClientCreditGate
{
    /// <summary>
    /// Modo de cobro EFECTIVO del pagador de la reserva. Si la reserva no tiene pagador (<c>PayerId</c> null) o
    /// el cliente no existe, cae a Prepaid (la posicion segura: sin cliente identificado no hay cuenta corriente
    /// que evaluar, asi que se exige el pago completo como en prepago).
    /// </summary>
    public static async Task<CustomerBillingMode> ResolveModeAsync(
        AppDbContext db,
        int? payerId,
        OperationalFinanceSettings settings,
        CancellationToken ct)
    {
        if (payerId is null)
        {
            return CustomerBillingMode.Prepaid;
        }

        var customerMode = await db.Customers
            .AsNoTracking()
            .Where(c => c.Id == payerId.Value)
            .Select(c => c.BillingMode)
            .FirstOrDefaultAsync(ct);

        // FirstOrDefaultAsync sobre un Nullable<enum> devuelve null si el cliente no existe O si su modo es null;
        // ambos casos heredan el default de la agencia, que es lo correcto.
        return ClientBillingModeResolver.Resolve(customerMode, settings.DefaultCustomerBillingMode);
    }

    /// <summary>
    /// Modos de cobro PROPIOS (sin resolver el default) de varios clientes en una sola pasada (anti N+1 del job):
    /// customerId -&gt; BillingMode (puede ser null = heredar default). Clientes sin fila no aparecen; el caller
    /// resuelve la ausencia con <see cref="ClientBillingModeResolver.Resolve"/> (cae al default de la agencia).
    /// </summary>
    public static async Task<Dictionary<int, CustomerBillingMode?>> GetBillingModesAsync(
        AppDbContext db,
        IReadOnlyCollection<int> customerIds,
        CancellationToken ct)
    {
        if (customerIds.Count == 0)
        {
            return new Dictionary<int, CustomerBillingMode?>();
        }

        var rows = await db.Customers
            .AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.BillingMode })
            .ToListAsync(ct);

        return rows.ToDictionary(x => x.Id, x => x.BillingMode);
    }

    /// <summary>
    /// Limites de credito por moneda de varios clientes en una sola pasada (anti N+1 del job):
    /// customerId -&gt; (moneda normalizada -&gt; limite). Clientes sin limites no aparecen.
    /// </summary>
    public static async Task<Dictionary<int, Dictionary<string, decimal>>> GetLimitsByCurrencyForCustomersAsync(
        AppDbContext db,
        IReadOnlyCollection<int> customerIds,
        CancellationToken ct)
    {
        if (customerIds.Count == 0)
        {
            return new Dictionary<int, Dictionary<string, decimal>>();
        }

        var rows = await db.CustomerCreditLimitByCurrency
            .AsNoTracking()
            .Where(limit => customerIds.Contains(limit.CustomerId))
            .Select(limit => new { limit.CustomerId, limit.Currency, limit.Limit })
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
            if (!byCurrency.TryGetValue(key, out var existing) || row.Limit > existing)
            {
                byCurrency[key] = row.Limit;
            }
        }

        return result;
    }

    /// <summary>
    /// Limites de credito del cliente por moneda (moneda normalizada -&gt; limite). Solo las monedas que TIENEN
    /// una fila de limite; la ausencia de una moneda la interpreta la politica como "prepago de esa moneda".
    /// </summary>
    public static async Task<Dictionary<string, decimal>> GetLimitsByCurrencyAsync(
        AppDbContext db,
        int customerId,
        CancellationToken ct)
    {
        var rows = await db.CustomerCreditLimitByCurrency
            .AsNoTracking()
            .Where(limit => limit.CustomerId == customerId)
            .Select(limit => new { limit.Currency, limit.Limit })
            .ToListAsync(ct);

        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            // Si por dato inconsistente hubiera dos filas que normalizan a la misma moneda, nos quedamos con la
            // mas alta (la mas permisiva): el indice unico (CustomerId, Currency) deberia evitarlo en la practica.
            var key = Monedas.Normalizar(row.Currency);
            if (!result.TryGetValue(key, out var existing) || row.Limit > existing)
            {
                result[key] = row.Limit;
            }
        }

        return result;
    }

    /// <summary>
    /// Construye el contexto de credito leyendo limites + exposicion FRESCA del cliente. La exposicion incluye
    /// las reservas "En viaje" (review B1). FASE 1: <c>IsInArrears</c> siempre false (la mora llega en Fase 2).
    /// </summary>
    public static async Task<ClientCreditContext> BuildContextAsync(
        AppDbContext db,
        int customerId,
        decimal thisReservaBalance,
        OperationalFinanceSettings settings,
        CancellationToken ct)
    {
        var limits = await GetLimitsByCurrencyAsync(db, customerId, ct);
        var exposure = await CustomerCreditExposureReader.GetExposureByCurrencyAsync(db, customerId, ct);

        return new ClientCreditContext(
            LimitsByCurrency: limits,
            ExposureByCurrency: exposure,
            // Punto de extension Fase 2: cuando existan vencimientos, aca se calcula la mora real del cliente.
            IsInArrears: false,
            BlockWhenOverLimit: settings.BlockTravelWhenCreditExceeded,
            ThisReservaBalance: thisReservaBalance);
    }

    /// <summary>
    /// Evalua el candado de credito para PASAR A VIAJAR a un cliente a cuenta. Asume que el caller ya resolvio
    /// que el modo es Account; para Prepaid el caller usa <c>ReservationEconomicPolicy.IsClientFullyPaid</c>.
    /// </summary>
    public static async Task<ClientCreditDecision> EvaluateCanTravelAsync(
        AppDbContext db,
        int customerId,
        decimal thisReservaBalance,
        OperationalFinanceSettings settings,
        CancellationToken ct)
    {
        var context = await BuildContextAsync(db, customerId, thisReservaBalance, settings, ct);
        return ClientCreditPolicy.EvaluateCanTravel(context);
    }
}
