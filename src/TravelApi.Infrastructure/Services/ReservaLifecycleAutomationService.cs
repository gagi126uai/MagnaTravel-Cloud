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
/// - Confirmed -&gt; Traveling: cuando StartDate &lt;= today Y el CLIENTE quedo SALDADO (ADR-036: candado duro
///   e incondicional). Tambien chequea capacidad. Si el cliente debe, el job NO promueve, loguea y reintenta.
/// - Traveling -&gt; Closed: cuando EndDate &lt; today AND Balance == 0 (IGUAL que el clasico).
///   El cierre por fin de viaje es DIRECTO por default.
/// - ADR-036 (2026-06-21, prepago puro): ToSettle ("a liquidar") MURIO. Ya no hay desvio de liquidacion: el
///   operador cobra el 100% antes del viaje. El job solo maneja Confirmed-&gt;Traveling y Traveling-&gt;Closed.
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
    // ADR-020 F3: motor de estados, para la pasada de reconciliacion nocturna.
    private readonly TravelApi.Infrastructure.Services.Reservations.ReservaAutoStateService _autoStateService;

    public ReservaLifecycleAutomationService(
        AppDbContext db,
        ILogger<ReservaLifecycleAutomationService> logger,
        IOperationalFinanceSettingsService settingsService,
        TravelApi.Infrastructure.Services.Reservations.ReservaAutoStateService autoStateService)
    {
        _db = db;
        _logger = logger;
        _settingsService = settingsService;
        _autoStateService = autoStateService;
    }

    public async Task<int> RunDailyAsync(CancellationToken ct = default)
    {
        var result = await RunDailyDetailedAsync(ct);
        return result.Promoted + result.Closed + result.Repaired + result.Reconciled;
    }

    /// <summary>
    /// Variante de RunDailyAsync que devuelve los counts separados
    /// (repaired/promoted/closed) para que el endpoint admin de mantenimiento
    /// muestre feedback util al operador.
    ///
    /// Orden importante: primero reparar EndDate desde servicios (sino el
    /// auto-cierre no las puede tocar), despues promote, despues close.
    ///
    /// El campo Closed del resultado representa "cuantas cerraron por fin de viaje": las
    /// Traveling-&gt;Closed (EndDate pasada + Balance == 0). ADR-036: ToSettle ya no existe.
    /// </summary>
    public async Task<LifecycleRunResult> RunDailyDetailedAsync(CancellationToken ct = default)
    {
        // ADR-020 F3 (cura del motor): re-evalua el motor sobre todas las reservas En gestion /
        // Confirmada y corrige las que hayan esquivado el chokepoint o quedado en la ventana entre
        // los dos saves. Corre PRIMERO para que el resto del job vea estados ya reconciliados.
        var reconciled = await ReconcileAutoStatesAsync(ct);

        var repaired = await AutoRepairTravelingDatesAsync(ct);
        var promoted = await AutoTransitionConfirmedToTravelingAsync(ct);

        // ADR-020: cierre por fin de viaje Traveling -> Closed (EndDate pasada + Balance <= 0). El
        // <= 0 (no == 0) cubre el saldo a favor: una reserva cuyo cliente pago de mas debe poder
        // cerrarse igual (coherente con el gate manual, que solo bloquea Balance > 0). ADR-036: ToSettle murio.
        var closed = await AutoTransitionTravelingToClosedAsync(ct);

        _logger.LogInformation(
            "Lifecycle automation finished. Reconciled (motor): {Reconciled}. Repaired: {Repaired}. Confirmed->Traveling: {Promoted}. Avanzadas a fin de viaje: {Closed}.",
            reconciled, repaired, promoted, closed);
        return new LifecycleRunResult(promoted, closed, repaired, reconciled);
    }

    /// <summary>
    /// ADR-020 F3 (reconciliacion nocturna): re-evalua el motor de estados sobre todas las reservas
    /// en En gestion / Confirmada y corrige las que quedaron desincronizadas (esquivaron el chokepoint
    /// post-commit o quedaron en la ventana entre los dos saves). Devuelve cuantas curo y lo loguea:
    /// un valor &gt; 0 sostenido es accionable (hay un chokepoint de mutacion sin cubrir).
    /// </summary>
    public async Task<int> ReconcileAutoStatesAsync(CancellationToken ct = default)
    {
        var candidateIds = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.InManagement || r.Status == EstadoReserva.Confirmed)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var cured = 0;
        foreach (var reservaId in candidateIds)
        {
            // EvaluateAndApplyAsync devuelve true si hubo un cambio de estado real. La reconciliacion
            // es una CURA en lote: NO notifica regresiones (suppressNotifications: true). Asi la primera
            // corrida tras el deploy, que regresa en masa historicas Confirmed con servicios sin
            // resolver, no dispara una avalancha de avisos. La franja naranja (LastRegressionReason) si
            // queda. Aca solo contamos cuantas curo.
            var changed = await _autoStateService.EvaluateAndApplyAsync(reservaId, suppressNotifications: true, ct);
            if (changed) cured++;
        }

        if (cured > 0)
        {
            _logger.LogWarning(
                "Lifecycle reconciliation: el motor curo {Cured} reserva(s) desincronizada(s). " +
                "Un valor sostenido > 0 indica un chokepoint de mutacion de servicio sin cubrir.",
                cured);
        }

        return cured;
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
    /// Promueve Confirmed -&gt; Traveling cuando arranca el viaje (StartDate &lt;= hoy) Y el CLIENTE quedo
    /// SALDADO. Siempre chequea capacidad. El chequeo de "servicios sin confirmar" depende del flag:
    ///  - flag OFF: lo chequea aca (igual que el flujo manual clasico).
    ///  - flag ON: NO lo chequea (ya se garantizo en Sold-&gt;Confirmed).
    ///
    /// <para>ADR-036 (2026-06-21, prepago puro): el job aplica el MISMO candado de pago del cliente que el
    /// pase manual (<c>ReservationEconomicPolicy.IsClientFullyPaid</c>, INCONDICIONAL — no mira la llave). Si
    /// el cliente todavia debe, el job NO promueve, lo cuenta como bloqueado y loguea, y reintentara en la
    /// proxima corrida (cuando llegue el cobro). NO lanza excepcion: un solo file con saldo no debe abortar la
    /// corrida de todos los demas.</para>
    /// </summary>
    public async Task<int> AutoTransitionConfirmedToTravelingAsync(CancellationToken ct = default)
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
            // Inconsistencia de capacidad pasajeros vs servicios bloquea el pase (independiente del
            // saldo). ADR-020: NO re-chequeamos servicios sin resolver — para estar en Confirmed el
            // motor ya garantizo que todos estan resueltos.
            var capacityReason = await ReservaCapacityRules.GetBlockReasonAsync(_db, reserva.Id, ct);
            if (!string.IsNullOrWhiteSpace(capacityReason))
            {
                blocked++;
                _logger.LogWarning(
                    "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Confirmed->Traveling por inconsistencia de capacidad: {Reason}",
                    reserva.Id, reserva.NumeroReserva, capacityReason);
                continue;
            }

            // ADR-036: candado DURO de pago del cliente. Si todavia debe, no viaja: se cuenta como bloqueado,
            // se loguea (sin montos) y se reintenta en la proxima corrida. Nunca excepcion en el job.
            if (!ReservationEconomicPolicy.IsClientFullyPaid(reserva.Balance))
            {
                blocked++;
                _logger.LogInformation(
                    "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Confirmed->Traveling: el cliente todavia tiene saldo pendiente. Se reintenta cuando quede saldada.",
                    reserva.Id, reserva.NumeroReserva);
                continue;
            }

            planned.Add(new PlannedTransition(
                Reserva: reserva,
                FromStatus: EstadoReserva.Confirmed,
                ToStatus: EstadoReserva.Traveling,
                StampClosedAt: false,
                WriteForwardLog: true,
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
    /// <para>ADR-036 (2026-06-21, prepago puro): el fin de viaje cierra directo Traveling -&gt; Closed (ya no
    /// existe el desvio ToSettle "a liquidar"). La unica diferencia es el rastro auditable: con flag ON se
    /// escribe ReservaStatusChangeLog (como el resto de la cadena nueva); con flag OFF no.</para>
    /// </summary>
    public async Task<int> AutoTransitionTravelingToClosedAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var candidates = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Traveling
                && r.EndDate.HasValue
                && r.EndDate.Value.Date < today
                && r.Balance <= 0) // ADR-020 (M3): <= 0 cubre el saldo a favor, coherente con el gate manual
            .ToListAsync(ct);

        var planned = candidates.Select(reserva => new PlannedTransition(
            Reserva: reserva,
            FromStatus: EstadoReserva.Traveling,
            ToStatus: EstadoReserva.Closed,
            StampClosedAt: true,
            WriteForwardLog: true,
            Reason: "Fin de viaje (EndDate pasada) con saldo saldado: cierre directo")).ToList();

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

            // CRM leads (fix de fondo 2026-06-18): si el job deja la reserva en un estado FIRME (el caso real
            // es Confirmed -> Traveling) y nacio de un lead, ese lead debe quedar Ganado. Normalmente ya lo
            // estara (llego a Confirmed via el motor, que tambien dispara el hook), pero lo evaluamos aca por
            // si alguna reserva alcanzo Traveling sin haber pasado por ese disparo. Idempotente: no toca un
            // lead ya Ganado/Perdido, y el cierre Traveling -> Closed (no firme) es no-op. Sin SaveChanges
            // propio: se persiste en el SaveChanges unico al final de la tanda.
            await Reservations.SourceLeadWonHook.MarkSourceLeadAsWonIfReservaIsFirmAsync(_db, reserva, ct);

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
/// Closed = cantidad que el job cerro Traveling->Closed (EndDate vencido + Balance cero).
///          ADR-036: ToSettle ya no existe.
/// </summary>
public record LifecycleRunResult(int Promoted, int Closed, int Repaired, int Reconciled = 0);
