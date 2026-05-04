using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Lifecycle automation: corre via Hangfire (ej. daily) y aplica las transiciones
/// que el negocio considera automaticas:
/// - Reserved -> Operational: si Balance == 0 OR StartDate <= today (el viaje arranca).
/// - Operational -> Closed: si EndDate < today (el viaje ya termino, al dia siguiente).
///
/// El indicador "En curso" se calcula en tiempo de lectura (ReservaService.ComputeIsInProgress)
/// y no requiere automatizacion.
/// </summary>
public class ReservaLifecycleAutomationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ReservaLifecycleAutomationService> _logger;

    public ReservaLifecycleAutomationService(AppDbContext db, ILogger<ReservaLifecycleAutomationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> RunDailyAsync(CancellationToken ct = default)
    {
        var result = await RunDailyDetailedAsync(ct);
        return result.Promoted + result.Closed + result.Repaired;
    }

    /// <summary>
    /// Variante de RunDailyAsync que devuelve los counts separados
    /// (repaired/promoted/closed) para que el endpoint admin de mantenimiento
    /// muestre feedback util al operador.
    ///
    /// Orden importante: primero reparar EndDate desde servicios (sino el
    /// auto-cierre no las puede tocar), despues promote, despues close.
    /// </summary>
    public async Task<LifecycleRunResult> RunDailyDetailedAsync(CancellationToken ct = default)
    {
        var repaired = await AutoRepairOperationalDatesAsync(ct);
        var promoted = await AutoTransitionReservedToOperationalAsync(ct);
        var closed = await AutoTransitionOperationalToClosedAsync(ct);
        _logger.LogInformation(
            "Lifecycle automation finished. Repaired: {Repaired}. Reserved->Operational: {Promoted}. Operational->Closed: {Closed}.",
            repaired, promoted, closed);
        return new LifecycleRunResult(promoted, closed, repaired);
    }

    /// <summary>
    /// Repara reservas en estado Operativo cuyo EndDate quedo en null pero
    /// tienen servicios cargados (ej. reservas viejas creadas antes de que
    /// existiera el recompute automatico). Computa la fecha desde los
    /// servicios y la persiste para que el auto-cierre pueda evaluarla.
    ///
    /// Si una reserva no tiene servicios (no podemos inferir), queda como esta.
    /// </summary>
    public async Task<int> AutoRepairOperationalDatesAsync(CancellationToken ct = default)
    {
        // Limite defensivo: si por alguna razon hay miles de reservas en este
        // estado, evitamos un OOM. La proxima corrida levanta el resto.
        var orphans = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Operational && r.EndDate == null)
            .Take(500)
            .ToListAsync(ct);

        var repaired = 0;
        foreach (var reserva in orphans)
        {
            var (start, end) = await ReservaScheduleCalculator.ComputeAsync(_db, reserva.Id, ct);
            if (!end.HasValue) continue; // sin servicios, no podemos inferir
            reserva.StartDate = start;
            reserva.EndDate = end;
            repaired++;
        }

        if (repaired > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Auto-repaired {Repaired} Reserva(s) Operational con EndDate=null.", repaired);
        }

        return repaired;
    }

    public async Task<int> AutoTransitionReservedToOperationalAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var candidates = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Reserved
                && (r.Balance <= 0 || (r.StartDate.HasValue && r.StartDate.Value.Date <= today)))
            .ToListAsync(ct);

        var promoted = 0;
        var blocked = 0;

        foreach (var reserva in candidates)
        {
            // Inconsistencia de capacidad pasajeros vs servicios bloquea el pase
            // (independiente del saldo). Igual que el flujo manual.
            var capacityReason = await ReservaCapacityRules.GetBlockReasonAsync(_db, reserva.Id, ct);
            if (!string.IsNullOrWhiteSpace(capacityReason))
            {
                blocked++;
                _logger.LogWarning(
                    "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Reserved->Operational por inconsistencia de capacidad: {Reason}",
                    reserva.Id, reserva.NumeroReserva, capacityReason);
                continue;
            }

            // Servicios sin confirmar con el proveedor.
            var unconfirmedReason = await ReservaCapacityRules.GetUnconfirmedServicesBlockReasonAsync(_db, reserva.Id, ct);
            if (!string.IsNullOrWhiteSpace(unconfirmedReason))
            {
                blocked++;
                _logger.LogWarning(
                    "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Reserved->Operational por servicios sin confirmar: {Reason}",
                    reserva.Id, reserva.NumeroReserva, unconfirmedReason);
                continue;
            }

            reserva.Status = EstadoReserva.Operational;
            promoted++;
        }

        if (promoted > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Auto-promoted {Promoted} Reserva(s) Reserved->Operational. Skipped {Blocked} por inconsistencia de capacidad.",
            promoted, blocked);

        return promoted;
    }

    public async Task<int> AutoTransitionOperationalToClosedAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var candidates = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Operational
                && r.EndDate.HasValue
                && r.EndDate.Value.Date < today)
            .ToListAsync(ct);

        foreach (var reserva in candidates)
        {
            reserva.Status = EstadoReserva.Closed;
            reserva.ClosedAt = DateTime.UtcNow;
        }

        if (candidates.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Auto-closed {Count} Reserva(s) Operational->Closed.", candidates.Count);
        }

        return candidates.Count;
    }
}

/// <summary>
/// Resultado de una corrida de lifecycle automation.
/// Repaired = cantidad de reservas Operativas con EndDate=null cuyas fechas
///            se reconstruyeron desde los servicios cargados.
/// Promoted = cantidad de reservas que pasaron Reserved -> Operational.
/// Closed = cantidad que pasaron Operational -> Closed.
/// </summary>
public record LifecycleRunResult(int Promoted, int Closed, int Repaired);
