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
        return result.Promoted + result.Closed;
    }

    /// <summary>
    /// Variante de RunDailyAsync que devuelve los counts separados (promoted/closed)
    /// para que el endpoint admin de mantenimiento muestre feedback util al operador.
    /// </summary>
    public async Task<LifecycleRunResult> RunDailyDetailedAsync(CancellationToken ct = default)
    {
        var promoted = await AutoTransitionReservedToOperationalAsync(ct);
        var closed = await AutoTransitionOperationalToClosedAsync(ct);
        _logger.LogInformation(
            "Lifecycle automation finished. Reserved->Operational: {Promoted}. Operational->Closed: {Closed}.",
            promoted, closed);
        return new LifecycleRunResult(promoted, closed);
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
/// Promoted = cantidad de reservas que pasaron Reserved -> Operational.
/// Closed = cantidad que pasaron Operational -> Closed.
/// </summary>
public record LifecycleRunResult(int Promoted, int Closed);
