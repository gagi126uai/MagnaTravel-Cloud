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
        var promoted = await AutoTransitionReservedToOperationalAsync(ct);
        var closed = await AutoTransitionOperationalToClosedAsync(ct);
        _logger.LogInformation(
            "Lifecycle automation finished. Reserved->Operational: {Promoted}. Operational->Closed: {Closed}.",
            promoted, closed);
        return promoted + closed;
    }

    public async Task<int> AutoTransitionReservedToOperationalAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var candidates = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Reserved
                && (r.Balance <= 0 || (r.StartDate.HasValue && r.StartDate.Value.Date <= today)))
            .ToListAsync(ct);

        foreach (var reserva in candidates)
        {
            reserva.Status = EstadoReserva.Operational;
        }

        if (candidates.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Auto-promoted {Count} Reserva(s) Reserved->Operational.", candidates.Count);
        }

        return candidates.Count;
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
