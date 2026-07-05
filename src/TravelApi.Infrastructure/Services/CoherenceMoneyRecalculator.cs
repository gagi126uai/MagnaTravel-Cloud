using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// (2026-07-04, hallazgo A1 de la auditoría) Recalcula la PLATA de las reservas anuladas cuya proyección
/// económica quedó desactualizada, dejándola como la dejaría una anulación moderna.
///
/// <para><b>Por qué existe</b>: las reservas anuladas viejas quedaron con servicios sin cancelar y, aunque la
/// migración <c>RepairLegacyAnnulledReservaServices</c> ya los cancela en la base, el SQL de esa migración NO
/// recalcula el saldo (eso necesita la lógica de dominio, no SQL crudo). Este servicio es el PASO 2: corre los
/// persisters canónicos (los MISMOS que usa la anulación moderna) para que la venta confirmada y el saldo bajen
/// a lo real y la cuenta corriente/alertas dejen de mostrar deuda fantasma.</para>
///
/// <para><b>Reutilizable</b>: se expone como servicio inyectable (no vive dentro del controller) a propósito, para
/// que el vigía nocturno de consistencia (pieza futura, Tanda 4) pueda llamarlo igual que el endpoint admin.</para>
///
/// <para><b>Idempotente</b>: los persisters recalculan la plata desde cero. Una segunda corrida sobre una reserva
/// ya sana produce el mismo número → no cuenta como corregida. Una reserva anulada con saldo a favor legítimo
/// (saldo negativo) o con multa por Nota de Débito (saldo positivo respaldado) también recalcula al mismo valor:
/// el recálculo NO duplica el saldo a favor ni inventa deuda.</para>
/// </summary>
public class CoherenceMoneyRecalculator
{
    private readonly AppDbContext _db;
    private readonly ILogger<CoherenceMoneyRecalculator> _logger;

    public CoherenceMoneyRecalculator(AppDbContext db, ILogger<CoherenceMoneyRecalculator> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Resultado del recálculo. Números crudos, sin nada técnico: el controller/vigía arman el texto para el usuario.
    /// </summary>
    /// <param name="Reviewed">Cuántas reservas anuladas se revisaron (candidatas con plata a chequear).</param>
    /// <param name="Corrected">Cuántas terminaron con un saldo distinto tras recalcular (estaban desactualizadas).</param>
    /// <param name="Failed">Cuántas no se pudieron recalcular por un error (quedan para reintentar).</param>
    public sealed record CoherenceRecalculationResult(int Reviewed, int Corrected, int Failed);

    // Estados terminales de anulación cuya plata se resuelve por el circuito de cancelación (no por cobro normal).
    // Fuente única de los literales: EstadoReserva. No se copian a mano.
    private static readonly string[] AnnulledStatuses =
    {
        EstadoReserva.Cancelled,
        EstadoReserva.PendingOperatorRefund
    };

    /// <summary>
    /// Revisa las reservas anuladas cuya proyección de plata se ve inconsistente (saldo o venta confirmada
    /// distintos de cero, sea en el escalar o en la tabla hija por moneda) y las recalcula con los persisters
    /// canónicos. Devuelve cuántas revisó, corrigió y falló.
    /// </summary>
    public async Task<CoherenceRecalculationResult> RecalculateAnnulledReservasMoneyAsync(CancellationToken ct = default)
    {
        var candidateReservaIds = await FindCandidateReservaIdsAsync(ct);

        int reviewed = 0;
        int corrected = 0;
        int failed = 0;

        foreach (var reservaId in candidateReservaIds)
        {
            ct.ThrowIfCancellationRequested();
            reviewed++;

            try
            {
                var wasCorrected = await RecalculateSingleReservaAsync(reservaId, ct);
                if (wasCorrected) corrected++;
            }
            catch (Exception ex)
            {
                // Aislar el fallo por reserva: una reserva con datos rotos no debe abortar el barrido entero.
                // No se filtra el detalle técnico al usuario; queda en el log del servidor con un id seguro.
                failed++;
                _logger.LogError(ex, "Recálculo de coherencia falló para la reserva {ReservaId}", reservaId);
            }
            finally
            {
                // Higiene de tracking: cada reserva se procesa aislada. Limpiar evita que las entidades cargadas
                // por los persisters de una reserva se acumulen o interfieran con la siguiente.
                _db.ChangeTracker.Clear();
            }
        }

        _logger.LogInformation(
            "Recálculo de coherencia de plata: {Reviewed} revisadas, {Corrected} corregidas, {Failed} con error.",
            reviewed, corrected, failed);

        return new CoherenceRecalculationResult(reviewed, corrected, failed);
    }

    /// <summary>
    /// Junta los ids de reservas anuladas que valen la pena revisar: las que tienen saldo o venta confirmada
    /// distintos de cero en el ESCALAR, más las que se ven inconsistentes en la tabla hija por moneda (cubre el
    /// caso "escalar en cero pero hija stale" y viceversa). Se resuelve en dos consultas simples (sin subconsulta
    /// correlacionada) para que también funcione en el provider InMemory de los tests.
    /// </summary>
    private async Task<HashSet<int>> FindCandidateReservaIdsAsync(CancellationToken ct)
    {
        // 1) Reservas anuladas cuyo ESCALAR ya se ve inconsistente.
        var scalarCandidateIds = await _db.Reservas
            .AsNoTracking()
            .Where(r => AnnulledStatuses.Contains(r.Status)
                        && (r.Balance != 0m || r.ConfirmedSale != 0m))
            .Select(r => r.Id)
            .ToListAsync(ct);

        // 2) Ids de reservas anuladas (para filtrar la tabla hija por pertenencia sin subconsulta correlacionada).
        var annulledReservaIds = await _db.Reservas
            .AsNoTracking()
            .Where(r => AnnulledStatuses.Contains(r.Status))
            .Select(r => r.Id)
            .ToListAsync(ct);
        var annulledIdSet = annulledReservaIds.ToHashSet();

        // 3) Reservas cuya tabla hija por moneda se ve inconsistente (sirve aunque el escalar esté en cero).
        var childInconsistentReservaIds = await _db.ReservaMoneyByCurrency
            .AsNoTracking()
            .Where(m => m.Balance != 0m || m.ConfirmedSale != 0m)
            .Select(m => m.ReservaId)
            .Distinct()
            .ToListAsync(ct);

        var candidates = new HashSet<int>(scalarCandidateIds);
        foreach (var reservaId in childInconsistentReservaIds)
        {
            if (annulledIdSet.Contains(reservaId))
                candidates.Add(reservaId);
        }

        return candidates;
    }

    /// <summary>
    /// Recalcula la plata de UNA reserva con los persisters canónicos y devuelve si el saldo (o cualquiera de los
    /// escalares económicos) cambió. Toma una foto ANTES (sin tracking), corre el recálculo y compara con el DESPUÉS.
    /// </summary>
    private async Task<bool> RecalculateSingleReservaAsync(int reservaId, CancellationToken ct)
    {
        var before = await ReadMoneyScalarsAsync(reservaId, ct);
        if (before is null) return false; // desapareció entre la selección y el recálculo; nada que hacer.

        // Recálculo canónico: mismos persisters y mismo orden que los pasos 1 y 2 de la anulación
        // moderna (BookingCancellationService.RecalculateMoneyAfterTotalCancellationAsync).
        // OJO: la anulación moderna tiene un paso 3 (SupplierCreditReconciler, pool de saldo a favor
        // del operador) que acá se OMITE a propósito: las anuladas legacy no tienen entries de ese
        // pool (es una feature posterior) y su deuda de operador ya no contaba por estado, así que
        // no hay nada que reconciliar — y mintear crédito retroactivo sería inventar plata.
        //   1) deuda de cada operador de la reserva — con los servicios ya cancelados, sus compras dejan de contar;
        await SupplierDebtPersister.PersistForReservaSuppliersAsync(_db, reservaId, ct);
        //   2) plata del cliente (+ comisión, vía el chokepoint de ReservaMoneyPersister): reescribe escalar + hija.
        await ReservaMoneyPersister.PersistAsync(_db, reservaId, ct);

        var after = await ReadMoneyScalarsAsync(reservaId, ct);
        if (after is null) return false;

        return before.Value != after.Value;
    }

    /// <summary>Foto sin tracking de los cinco escalares económicos de una reserva (o null si no existe).</summary>
    private async Task<MoneyScalars?> ReadMoneyScalarsAsync(int reservaId, CancellationToken ct)
    {
        // Se proyecta a un tipo anónimo (clase) para que FirstOrDefaultAsync devuelva null cuando la reserva no
        // existe; una proyección directa a struct devolvería default(struct), que no distingue "no existe".
        var row = await _db.Reservas
            .AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => new { r.TotalSale, r.ConfirmedSale, r.TotalCost, r.TotalPaid, r.Balance })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        return new MoneyScalars(
            row.TotalSale, row.ConfirmedSale, row.TotalCost, row.TotalPaid, row.Balance);
    }

    /// <summary>Tupla de los escalares económicos; la igualdad estructural del record detecta el cambio.</summary>
    private readonly record struct MoneyScalars(
        decimal TotalSale, decimal ConfirmedSale, decimal TotalCost, decimal TotalPaid, decimal Balance);
}
