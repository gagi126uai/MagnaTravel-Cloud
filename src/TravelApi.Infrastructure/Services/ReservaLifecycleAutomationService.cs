using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Lifecycle automation: corre via Hangfire (ej. daily) y aplica las transiciones
/// que el negocio considera automaticas:
/// - Confirmed -> Traveling: cuando StartDate &lt;= today (el viaje arranca).
///   Antes esta transicion tambien disparaba si Balance &lt;= 0; eso se elimino
///   porque "pago todo" es financiero, no temporal — ahora se modela como chip
///   derivado "Pagada" sin cambiar el estado.
/// - Traveling -> Closed: cuando EndDate &lt; today AND Balance == 0 (el viaje
///   termino y no quedan pendientes financieros). Antes el job no chequeaba
///   balance y cerraba reservas con saldo pendiente, oculto al operador.
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
        var repaired = await AutoRepairTravelingDatesAsync(ct);
        var promoted = await AutoTransitionConfirmedToTravelingAsync(ct);
        var closed = await AutoTransitionTravelingToClosedAsync(ct);
        _logger.LogInformation(
            "Lifecycle automation finished. Repaired: {Repaired}. Confirmed->Traveling: {Promoted}. Traveling->Closed: {Closed}.",
            repaired, promoted, closed);
        return new LifecycleRunResult(promoted, closed, repaired);
    }

    /// <summary>
    /// Repara reservas en estado Traveling cuyo EndDate quedo en null pero
    /// tienen servicios cargados (ej. reservas viejas creadas antes de que
    /// existiera el recompute automatico). Computa la fecha desde los
    /// servicios y la persiste para que el auto-cierre pueda evaluarla.
    ///
    /// Si una reserva no tiene servicios (no podemos inferir), queda como esta.
    /// </summary>
    public async Task<int> AutoRepairTravelingDatesAsync(CancellationToken ct = default)
    {
        // Limite defensivo: si por alguna razon hay miles de reservas en este
        // estado, evitamos un OOM. La proxima corrida levanta el resto.
        var orphans = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Traveling && r.EndDate == null)
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
            _logger.LogInformation("Auto-repaired {Repaired} Reserva(s) Traveling con EndDate=null.", repaired);
        }

        return repaired;
    }

    /// <summary>
    /// Promueve Confirmed -> Traveling cuando arranca el viaje (StartDate &lt;= hoy).
    /// La regla anterior tambien disparaba si Balance == 0 (cliente pago todo) pero
    /// se elimino: pagar es un evento financiero (chip "Pagada"), no operativo.
    /// La reserva sigue en Confirmed hasta que efectivamente arranque el viaje.
    /// </summary>
    public async Task<int> AutoTransitionConfirmedToTravelingAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var candidates = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Confirmed
                && r.StartDate.HasValue && r.StartDate.Value.Date <= today)
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
                    "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Confirmed->Traveling por inconsistencia de capacidad: {Reason}",
                    reserva.Id, reserva.NumeroReserva, capacityReason);
                continue;
            }

            // Servicios sin confirmar con el proveedor.
            var unconfirmedReason = await ReservaCapacityRules.GetUnconfirmedServicesBlockReasonAsync(_db, reserva.Id, ct);
            if (!string.IsNullOrWhiteSpace(unconfirmedReason))
            {
                blocked++;
                _logger.LogWarning(
                    "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Confirmed->Traveling por servicios sin confirmar: {Reason}",
                    reserva.Id, reserva.NumeroReserva, unconfirmedReason);
                continue;
            }

            reserva.Status = EstadoReserva.Traveling;
            promoted++;
        }

        if (promoted > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Auto-promoted {Promoted} Reserva(s) Confirmed->Traveling. Skipped {Blocked} por inconsistencia de capacidad o servicios sin confirmar.",
            promoted, blocked);

        return promoted;
    }

    /// <summary>
    /// Cierra Traveling -> Closed cuando el viaje ya termino (EndDate &lt; hoy)
    /// Y NO hay saldo pendiente (Balance == 0). Antes el job no chequeaba balance
    /// y cerraba reservas con deuda, ocultandola. Ahora unificado con el cierre
    /// manual: las reservas con EndDate vencido pero saldo pendiente quedan en
    /// Traveling y se ven en la UI con chip "Vencida con deuda".
    /// </summary>
    public async Task<int> AutoTransitionTravelingToClosedAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var candidates = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Traveling
                && r.EndDate.HasValue
                && r.EndDate.Value.Date < today
                && r.Balance == 0)
            .ToListAsync(ct);

        foreach (var reserva in candidates)
        {
            reserva.Status = EstadoReserva.Closed;
            reserva.ClosedAt = DateTime.UtcNow;
        }

        if (candidates.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Auto-closed {Count} Reserva(s) Traveling->Closed.", candidates.Count);
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
