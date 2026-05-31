using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Lifecycle automation: corre via Hangfire (ej. daily) y aplica las transiciones
/// que el negocio considera automaticas. El comportamiento depende del flag
/// <c>EnableSoldToSettleStates</c> (rediseño Fase A+B, 2026-05-30):
///
/// <para>CICLO CLASICO (flag OFF, default historico):</para>
/// - Confirmed -&gt; Traveling: cuando StartDate &lt;= today (el viaje arranca). Chequea
///   capacidad + servicios sin confirmar.
/// - Traveling -&gt; Closed: cuando EndDate &lt; today AND Balance == 0.
///
/// <para>CICLO NUEVO (flag ON):</para>
/// - Confirmed -&gt; Traveling: cuando StartDate &lt;= today. Solo chequea capacidad (los
///   servicios sin confirmar ya se garantizaron en Sold-&gt;Confirmed).
/// - Traveling -&gt; Closed: cuando EndDate &lt; today AND Balance == 0 (IGUAL que el clasico).
///   El cierre por fin de viaje es DIRECTO por default.
/// - ToSettle ("a liquidar") quedo FUERA del job: es un DESVIO MANUAL OPCIONAL. El job NUNCA
///   mete a nadie en ToSettle ni lo auto-cierra; entra y sale de ToSettle solo a mano (el
///   usuario aparca la reserva ahi a proposito y la cierra cuando arregla cuentas con el operador).
///
/// <para>Concurrencia: el review descarto el concurrency token xmin en Reserva (se activaba con
/// el flag apagado y exponia caminos viejos a DbUpdateConcurrencyException). Como defensa minima,
/// antes de persistir cada cambio el job re-lee el estado actual de la fila en la base y solo la
/// mueve si sigue en el estado origen esperado; si un cajero la movio a mano en el medio, la salta
/// y la loguea. La concurrencia fina (locking optimista por fila) queda como mejora futura.</para>
/// </summary>
public class ReservaLifecycleAutomationService
{
    // FIX 5 (A1, 2026-05-30): identidad del actor "sistema" cuando el job mueve una reserva por la
    // cadena nueva. Se persiste en ReservaStatusChangeLog para que la auditoria distinga las
    // transiciones automaticas del job de las que hace una persona.
    private const string SystemActorUserId = "system:lifecycle";
    private const string SystemActorUserName = "Sistema (lifecycle)";

    private readonly AppDbContext _db;
    private readonly ILogger<ReservaLifecycleAutomationService> _logger;
    private readonly IOperationalFinanceSettingsService _settingsService;

    public ReservaLifecycleAutomationService(
        AppDbContext db,
        ILogger<ReservaLifecycleAutomationService> logger,
        IOperationalFinanceSettingsService settingsService)
    {
        _db = db;
        _logger = logger;
        _settingsService = settingsService;
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
    ///
    /// El campo Closed del resultado representa "cuantas cerraron por fin de viaje": en ambos
    /// ciclos son las Traveling-&gt;Closed (EndDate pasada + Balance == 0). ToSettle NO participa
    /// del job: es un desvio manual opcional.
    /// </summary>
    public async Task<LifecycleRunResult> RunDailyDetailedAsync(CancellationToken ct = default)
    {
        var settings = await _settingsService.GetEntityAsync(ct);
        var soldToSettleEnabled = settings.EnableSoldToSettleStates;

        var repaired = await AutoRepairTravelingDatesAsync(ct);
        var promoted = await AutoTransitionConfirmedToTravelingAsync(soldToSettleEnabled, ct);

        // Cierre por fin de viaje: Traveling -> Closed (EndDate pasada + Balance == 0) en AMBOS
        // ciclos. ToSettle ("a liquidar") es un desvio MANUAL OPCIONAL: el job nunca lo crea ni lo
        // cierra. La unica diferencia entre ciclos es que el ciclo nuevo (flag ON) deja rastro
        // auditable (ReservaStatusChangeLog), igual que el resto de las transiciones de la cadena nueva.
        var closed = await AutoTransitionTravelingToClosedAsync(soldToSettleEnabled, ct);

        _logger.LogInformation(
            "Lifecycle automation finished (soldToSettle={Flag}). Repaired: {Repaired}. Confirmed->Traveling: {Promoted}. Avanzadas a fin de viaje: {Closed}.",
            soldToSettleEnabled, repaired, promoted, closed);
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
            // Esto NO cambia el Status (solo rellena fechas faltantes), asi que no aplica el
            // re-chequeo de transicion. Un solo SaveChanges, como siempre.
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Auto-repaired {Repaired} Reserva(s) Traveling con EndDate=null.", repaired);
        }

        return repaired;
    }

    /// <summary>
    /// Promueve Confirmed -&gt; Traveling cuando arranca el viaje (StartDate &lt;= hoy).
    /// Siempre chequea capacidad. El chequeo de "servicios sin confirmar" depende del flag:
    ///  - flag OFF: lo chequea aca (igual que el flujo manual clasico).
    ///  - flag ON: NO lo chequea (ya se garantizo en Sold-&gt;Confirmed).
    /// </summary>
    public async Task<int> AutoTransitionConfirmedToTravelingAsync(bool soldToSettleEnabled, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var candidates = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Confirmed
                && r.StartDate.HasValue && r.StartDate.Value.Date <= today)
            .ToListAsync(ct);

        var planned = new List<PlannedTransition>();
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

            if (!soldToSettleEnabled)
            {
                // Ciclo clasico: aca todavia se chequean los servicios sin confirmar, porque
                // en el ciclo clasico ese gate no se garantizo en un paso anterior.
                var unconfirmedReason = await ReservaCapacityRules.GetUnconfirmedServicesBlockReasonAsync(_db, reserva.Id, ct);
                if (!string.IsNullOrWhiteSpace(unconfirmedReason))
                {
                    blocked++;
                    _logger.LogWarning(
                        "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Confirmed->Traveling por servicios sin confirmar: {Reason}",
                        reserva.Id, reserva.NumeroReserva, unconfirmedReason);
                    continue;
                }
            }

            // FIX 5: Confirmed->Traveling existe en ambos ciclos. Solo dejamos rastro auditable
            // cuando estamos en la cadena nueva (flag ON); el ciclo clasico no se loguea (deuda
            // preexistente, fuera de scope).
            planned.Add(new PlannedTransition(
                Reserva: reserva,
                FromStatus: EstadoReserva.Confirmed,
                ToStatus: EstadoReserva.Traveling,
                StampClosedAt: false,
                WriteForwardLog: soldToSettleEnabled,
                Reason: "Inicio de viaje (StartDate alcanzada)"));
        }

        var promoted = await ApplyTransitionsAsync(planned, "AutoTransitionConfirmedToTraveling", ct);

        _logger.LogInformation(
            "Auto-promoted {Promoted} Reserva(s) Confirmed->Traveling. Skipped {Blocked} por inconsistencia de capacidad o servicios sin confirmar.",
            promoted, blocked);

        return promoted;
    }

    /// <summary>
    /// AMBOS CICLOS: cierra Traveling -&gt; Closed cuando el viaje ya termino (EndDate &lt; hoy) Y
    /// NO hay saldo pendiente (Balance == 0). Las reservas con EndDate vencido pero saldo
    /// pendiente quedan en Traveling y se ven con chip "Vencida con deuda".
    ///
    /// <para>Este es el cierre por DEFAULT en el ciclo nuevo: ToSettle ("a liquidar") es un desvio
    /// manual opcional, asi que el fin de viaje cierra directo igual que en el clasico. La unica
    /// diferencia es el rastro auditable: con flag ON se escribe ReservaStatusChangeLog (como el
    /// resto de la cadena nueva); con flag OFF no (deuda preexistente, fuera de scope del FIX 5).</para>
    /// </summary>
    public async Task<int> AutoTransitionTravelingToClosedAsync(bool soldToSettleEnabled, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var candidates = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Traveling
                && r.EndDate.HasValue
                && r.EndDate.Value.Date < today
                && r.Balance == 0)
            .ToListAsync(ct);

        // WriteForwardLog depende del flag: ON = cadena nueva (deja rastro), OFF = clasico (no).
        var planned = candidates.Select(reserva => new PlannedTransition(
            Reserva: reserva,
            FromStatus: EstadoReserva.Traveling,
            ToStatus: EstadoReserva.Closed,
            StampClosedAt: true,
            WriteForwardLog: soldToSettleEnabled,
            Reason: soldToSettleEnabled ? "Fin de viaje (EndDate pasada) con saldo cero: cierre directo" : null)).ToList();

        var saved = await ApplyTransitionsAsync(planned, "AutoTransitionTravelingToClosed", ct);
        if (saved > 0)
            _logger.LogInformation("Auto-closed {Count} Reserva(s) Traveling->Closed.", saved);
        return saved;
    }

    /// <summary>
    /// Aplica una tanda de transiciones de estado del job y persiste una sola vez.
    ///
    /// <para>Defensa de concurrencia (reemplaza el viejo xmin, descartado en el review): justo antes
    /// de mover cada reserva re-leemos su estado ACTUAL en la base (query AsNoTracking). Si en el
    /// medio un cajero la movio a mano (ya no esta en el estado origen que el job espera), la
    /// salteamos y la logueamos en vez de pisar su cambio. La concurrencia fina (lock por fila)
    /// queda como mejora futura; este re-chequeo cubre el caso comun job-vs-cajero.</para>
    ///
    /// <para>FIX 5 (A1): si la transicion pertenece a la cadena nueva (flag ON), se escribe un
    /// <see cref="ReservaStatusChangeLog"/> con Direction="Forward" y actor "sistema" para dejar
    /// rastro auditable. Las transiciones del ciclo clasico NO se loguean (deuda preexistente,
    /// fuera de scope).</para>
    /// </summary>
    private async Task<int> ApplyTransitionsAsync(
        List<PlannedTransition> planned,
        string operation,
        CancellationToken ct)
    {
        if (planned.Count == 0) return 0;

        var applied = 0;
        var now = DateTime.UtcNow;

        foreach (var transition in planned)
        {
            var reserva = transition.Reserva;

            // Defensa de concurrencia: re-leer el estado actual en la base. Si cambio respecto del
            // origen esperado, otra transaccion la toco -> saltear sin pisar.
            var currentStatusInDb = await _db.Reservas
                .AsNoTracking()
                .Where(r => r.Id == reserva.Id)
                .Select(r => r.Status)
                .FirstOrDefaultAsync(ct);

            if (!string.Equals(currentStatusInDb, transition.FromStatus, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Lifecycle {Operation}: Reserva {ReservaId} saltada. Esperaba origen '{From}' pero en la base esta en '{Current}' (otra transaccion la modifico). Se reevalua en la proxima corrida.",
                    operation, reserva.Id, transition.FromStatus, currentStatusInDb);
                continue;
            }

            reserva.Status = transition.ToStatus;
            if (transition.StampClosedAt)
                reserva.ClosedAt = now;

            // FIX 5 (A1): rastro auditable solo para transiciones de la cadena nueva.
            if (transition.WriteForwardLog)
            {
                _db.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
                {
                    ReservaId = reserva.Id,
                    FromStatus = transition.FromStatus,
                    ToStatus = transition.ToStatus,
                    Direction = "Forward",
                    ByUserId = SystemActorUserId,
                    ByUserName = SystemActorUserName,
                    Reason = transition.Reason,
                    OccurredAt = now
                });
            }

            applied++;
        }

        if (applied == 0) return 0;

        // Persistencia unica al final. Con el re-chequeo de arriba, last-write-wins solo puede
        // perderse en la ventana entre el re-read y el SaveChanges (muy chica). Aceptable: la
        // concurrencia fina es mejora futura.
        await _db.SaveChangesAsync(ct);
        return applied;
    }

    /// <summary>
    /// Una transicion planificada por el job: la reserva a mover, su estado origen esperado
    /// (para el re-chequeo de concurrencia), el destino, si estampar ClosedAt y si debe dejar
    /// rastro auditable (solo la cadena nueva, FIX 5).
    /// </summary>
    private sealed record PlannedTransition(
        Reserva Reserva,
        string FromStatus,
        string ToStatus,
        bool StampClosedAt,
        bool WriteForwardLog,
        string? Reason);
}

/// <summary>
/// Resultado de una corrida de lifecycle automation.
/// Repaired = cantidad de reservas Operativas con EndDate=null cuyas fechas
///            se reconstruyeron desde los servicios cargados.
/// Promoted = cantidad de reservas que pasaron Confirmed -> Traveling.
/// Closed = cantidad que el job cerro Traveling->Closed (EndDate vencido + Balance cero), en AMBOS
///          ciclos. El job NO mueve a ToSettle (es un desvio manual opcional, fuera del automatico).
/// </summary>
public record LifecycleRunResult(int Promoted, int Closed, int Repaired);
