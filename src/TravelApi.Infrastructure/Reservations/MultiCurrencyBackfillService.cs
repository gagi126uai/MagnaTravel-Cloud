using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-021 §7.2.6 (multimoneda, 2026-06-08): backfill IDEMPOTENTE de las tablas hijas materializadas
/// (<c>ReservaMoneyByCurrency</c> / <c>SupplierBalanceByCurrency</c>) para los datos legacy.
///
/// <para><b>Por que existe</b>: la migracion M1 crea las tablas hijas VACIAS. Hasta que cada reserva /
/// proveedor se recalcule por una operacion normal, su detalle por moneda no existe. Los consumidores
/// que leen la hija (cuenta corriente, reportes, top-N — Capa 6) verian saldo CERO para esas entidades
/// = un "dato silencioso falso" (un cliente que debe aparece sin deuda). Este paso de arranque puebla
/// la hija para todo el universo con saldo pendiente, de una.</para>
///
/// <para><b>Alcance FIJO (§7.2.6 endurecido, riesgo R-18)</b>: las reservas se recorren por
/// <c>Balance != 0</c> SIN filtrar por estado (el criterio es "tiene plata pendiente", no "esta
/// activa"). Para proveedores se reusa <c>RecalculateAllBalancesAsync</c>, que ya sincroniza la hija
/// de TODOS los proveedores (los saldados quedan sin filas, los deudores con su detalle por moneda).</para>
///
/// <para><b>Reusa la unica logica de calculo</b>: no duplica formulas. Las reservas pasan por
/// <c>ReservaMoneyPersister.PersistAsync</c> y los proveedores por <c>ISupplierService</c> — los mismos
/// caminos que la operacion normal. Por eso es idempotente y seguro de re-ejecutar en cada arranque.</para>
/// </summary>
public sealed class MultiCurrencyBackfillService
{
    private readonly AppDbContext _db;
    private readonly ISupplierService _supplierService;
    private readonly ILogger<MultiCurrencyBackfillService>? _logger;

    public MultiCurrencyBackfillService(
        AppDbContext db,
        ISupplierService supplierService,
        ILogger<MultiCurrencyBackfillService>? logger = null)
    {
        _db = db;
        _supplierService = supplierService;
        _logger = logger;
    }

    /// <summary>
    /// Chequeo barato para saltar el backfill cuando ya esta hecho: hay backfill pendiente si existe
    /// alguna reserva con <c>Balance != 0</c> sin fila hija, o algun proveedor con <c>CurrentBalance != 0</c>
    /// sin fila hija. Evita recorrer todo en cada arranque una vez poblado.
    /// </summary>
    public async Task<bool> NeedsBackfillAsync(CancellationToken ct = default)
    {
        bool anyReservaPending = await _db.Reservas
            .Where(r => r.Balance != 0m)
            .Where(r => !_db.ReservaMoneyByCurrency.Any(child => child.ReservaId == r.Id))
            .AnyAsync(ct);
        if (anyReservaPending) return true;

        bool anySupplierPending = await _db.Suppliers
            .Where(s => s.CurrentBalance != 0m)
            .Where(s => !_db.SupplierBalanceByCurrency.Any(child => child.SupplierId == s.Id))
            .AnyAsync(ct);
        return anySupplierPending;
    }

    /// <summary>
    /// Ejecuta el backfill. Devuelve cuantas reservas se poblaron (las del universo saldo != 0) y deja
    /// la hija de proveedores sincronizada via <c>RecalculateAllBalancesAsync</c>. Seguro de re-llamar.
    /// </summary>
    public async Task<(int reservas, int suppliers)> RunAsync(CancellationToken ct = default)
    {
        int reservasDone = await BackfillReservasAsync(ct);

        // Proveedores: reusamos la rutina consolidada que ya sincroniza escalar + tabla hija de TODOS
        // los proveedores. Es idempotente (upsert por moneda). Devuelve el universo deudor poblado.
        int supplierUniverse = await _db.Suppliers.CountAsync(s => s.CurrentBalance != 0m, ct);
        await _supplierService.RecalculateAllBalancesAsync(ct);

        _logger?.LogInformation(
            "ADR-021 multicurrency backfill done. Reservas pobladas={Reservas}, Proveedores deudores={Suppliers}.",
            reservasDone, supplierUniverse);

        return (reservasDone, supplierUniverse);
    }

    private async Task<int> BackfillReservasAsync(CancellationToken ct)
    {
        // Solo los Ids: el persister recarga el grafo economico de cada reserva. Universo = saldo
        // legacy != 0, SIN filtro de estado (R-18).
        var reservaIds = await _db.Reservas
            .Where(r => r.Balance != 0m)
            .Select(r => r.Id)
            .ToListAsync(ct);

        foreach (var reservaId in reservaIds)
        {
            // Cada reserva persiste escalar + hija en su propia SaveChanges (dentro del persister).
            // Idempotente: un re-run completa las que falten sin duplicar.
            await ReservaMoneyPersister.PersistAsync(_db, reservaId, ct);
        }

        return reservaIds.Count;
    }
}
