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

        // ADR-036 (saneamiento): cerrar las reservas que ya quedaron En viaje SIN servicios. Corre junto al
        // resto del housekeeping de Traveling y antes del cierre por fin de viaje (una vacia no tiene EndDate,
        // asi que el cierre normal no la alcanzaria; este barrido la cierra por estar vacia).
        var emptyTravelingClosed = await AutoCloseEmptyTravelingAsync(ct);

        var promoted = await AutoTransitionConfirmedToTravelingAsync(ct);

        // ADR-020: cierre por fin de viaje Traveling -> Closed (EndDate pasada + Balance <= 0). El
        // <= 0 (no == 0) cubre el saldo a favor: una reserva cuyo cliente pago de mas debe poder
        // cerrarse igual (coherente con el gate manual, que solo bloquea Balance > 0). ADR-036: ToSettle murio.
        var closed = await AutoTransitionTravelingToClosedAsync(ct);

        _logger.LogInformation(
            "Lifecycle automation finished. Reconciled (motor): {Reconciled}. Repaired: {Repaired}. En viaje vacias cerradas (saneamiento): {EmptyTravelingClosed}. Confirmed->Traveling: {Promoted}. Avanzadas a fin de viaje: {Closed}.",
            reconciled, repaired, emptyTravelingClosed, promoted, closed);
        // El saneamiento cuenta como cierres: se suma al count Closed del resultado.
        return new LifecycleRunResult(promoted, closed + emptyTravelingClosed, repaired, reconciled);
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

            // ADR-036 (2026-06-22): una reserva NO puede pasar a "En viaje" sin servicios cargados. Si quedo
            // Confirmed sin un solo servicio de ningun tipo, el job NO la promueve: se cuenta como bloqueada,
            // se loguea y se reintenta cuando alguien le cargue algun servicio. (En la practica una reserva
            // sin servicios no deberia llegar a Confirmed —el motor exige servicios resueltos—, pero esto
            // blinda el lifecycle contra reservas viejas o datos inconsistentes.)
            if (!await ReservaCapacityRules.HasAnyServiceAsync(_db, reserva.Id, ct))
            {
                blocked++;
                _logger.LogInformation(
                    "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Confirmed->Traveling: no tiene ningun servicio cargado. Se reintenta cuando se cargue al menos un servicio.",
                    reserva.Id, reserva.NumeroReserva);
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
            "Auto-promoted {Promoted} Reserva(s) Confirmed->Traveling. Skipped {Blocked} por inconsistencia de capacidad, sin servicios cargados o saldo del cliente pendiente.",
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
    /// ADR-036 (2026-06-22, saneamiento de una sola vez): cierra Traveling -&gt; Closed las reservas que YA
    /// quedaron atascadas en "En viaje" SIN ningun servicio cargado. El dueño eligio cerrarlas (no devolverlas
    /// a un estado anterior): son reservas vacias que nunca debieron viajar, y con el guard nuevo
    /// (<see cref="AutoTransitionConfirmedToTravelingAsync"/>) ya no se generan mas.
    ///
    /// <para>Reusa <see cref="ApplyTransitionsAsync"/> para heredar el rastro auditable
    /// (<see cref="ReservaStatusChangeLog"/> con actor "sistema") y el re-chequeo de concurrencia, igual que
    /// el cierre normal por fin de viaje. La diferencia es el motivo del log (saneamiento) y el filtro:
    /// estado Traveling + cero servicios, SIN mirar EndDate ni Balance (una reserva vacia no tiene plata
    /// operativa real ni fechas de servicios).</para>
    ///
    /// <para>Limite defensivo .Take(500), mismo patron que <see cref="AutoRepairTravelingDatesAsync"/>: si por
    /// alguna razon hubiera muchas, la proxima corrida levanta el resto. Como el guard nuevo evita que se
    /// creen nuevas, este barrido se vacia solo una vez aplicado. Se ordena por Id antes del Take para que el
    /// barrido sea determinista (la misma corrida levanta siempre el mismo lote).</para>
    ///
    /// <para><b>GUARD DE PLATA (review de seguridad ADR-036, 2026-06-22)</b>: el cierre es IRREVERSIBLE y el
    /// filtro "cero servicios" NO ve la plata — los cobros del cliente (<see cref="Payment"/>) y las facturas
    /// con CAE (<see cref="Invoice"/>) se cuelgan de la RESERVA (ReservaId), no de los servicios. Una reserva
    /// vacia PERO con plata viva (un cobro, una factura con CAE, o Balance != 0 — ej. un saldo a favor por un
    /// pago sin venta) es un PROBLEMA de plata, no una reserva descartable. Cerrarla lo escondería. Por eso una
    /// vacia con cualquiera de esas tres condiciones NO se cierra: se loguea para revision manual (sin montos,
    /// respetando el enmascarado see_cost) y se saltea. Solo se cierran las vacias-de-verdad, sin plata.</para>
    /// </summary>
    public async Task<int> AutoCloseEmptyTravelingAsync(CancellationToken ct = default)
    {
        var travelingIds = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Traveling)
            .OrderBy(r => r.Id) // barrido determinista: la misma corrida levanta siempre el mismo lote
            .Select(r => r.Id)
            .Take(500)
            .ToListAsync(ct);

        var planned = new List<PlannedTransition>();
        // IDs salteados por tener plata viva: se loguean al final para que un humano los revise (no se cierran).
        var skippedForLiveMoney = new List<(int Id, string NumeroReserva)>();

        foreach (var reservaId in travelingIds)
        {
            // Solo las VACIAS (cero servicios de todo tipo). Las que tienen aunque sea un servicio NO se tocan.
            if (await ReservaCapacityRules.HasAnyServiceAsync(_db, reservaId, ct))
                continue;

            // GUARD DE PLATA: aunque este vacia de servicios, si tiene plata viva NO se cierra (ver doc).
            if (await HasLiveMoneyAsync(reservaId, ct))
            {
                var numero = await _db.Reservas.AsNoTracking()
                    .Where(r => r.Id == reservaId)
                    .Select(r => r.NumeroReserva)
                    .FirstOrDefaultAsync(ct) ?? "(sin numero)";
                skippedForLiveMoney.Add((reservaId, numero));
                continue;
            }

            // Necesitamos la entidad rastreada para que ApplyTransitionsAsync mute Status/ClosedAt y persista.
            // Defensivo: entre la lista de IDs y este re-fetch alguien pudo borrar la reserva -> saltear.
            var reserva = await _db.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId, ct);
            if (reserva is null) continue;

            planned.Add(new PlannedTransition(
                Reserva: reserva,
                FromStatus: EstadoReserva.Traveling,
                ToStatus: EstadoReserva.Closed,
                StampClosedAt: true,
                WriteForwardLog: true,
                Reason: "Cierre de reserva en viaje sin servicios (saneamiento ADR-036)"));
        }

        // Aviso de revision manual de las vacias-con-plata: sin montos (solo Id + NumeroReserva).
        if (skippedForLiveMoney.Count > 0)
        {
            var detalle = string.Join(", ",
                skippedForLiveMoney.Select(x => $"{x.Id} ({x.NumeroReserva})"));
            _logger.LogWarning(
                "Saneamiento ADR-036: {Count} reserva(s) En viaje SIN servicios pero CON plata viva (cobro, factura con CAE o saldo distinto de cero) NO se cerraron. Requieren revision manual: {Reservas}.",
                skippedForLiveMoney.Count, detalle);
        }

        var closed = await ApplyTransitionsAsync(planned, "AutoCloseEmptyTraveling", ct);
        if (closed > 0)
        {
            // Logueamos la LISTA de IDs cerrados (no solo el count) para que el cierre irreversible sea auditable.
            var closedIds = string.Join(", ", planned.Select(p => p.Reserva.Id));
            _logger.LogWarning(
                "Saneamiento ADR-036: cerradas {Count} reserva(s) que estaban En viaje sin ningun servicio cargado. IDs: {ClosedIds}.",
                closed, closedIds);
        }

        // Tope del .Take alcanzado: hubo cierre masivo y quedo remanente para la proxima corrida. Lo avisamos
        // explicito para que no pase desapercibido (un barrido de 500 de una vez no deberia ser normal).
        if (travelingIds.Count == 500)
        {
            _logger.LogWarning(
                "Saneamiento ADR-036: se proceso el tope de 500 reservas En viaje en esta corrida. Puede haber remanente: la proxima corrida levanta el resto.");
        }

        return closed;
    }

    /// <summary>
    /// Guard de plata del saneamiento (review de seguridad ADR-036): true si la reserva tiene CUALQUIER rastro
    /// de plata viva colgando de ELLA (no de sus servicios), lo que impide cerrarla a ciegas. Tres condiciones,
    /// con OR-corto:
    /// <list type="number">
    ///   <item><b>Balance != 0</b> con la tolerancia de moneda de <see cref="ReservationEconomicPolicy"/> (NO
    ///     compara decimales crudos): cubre deuda Y saldo a favor (ej. un pago sin venta).</item>
    ///   <item><b>Algun <see cref="Payment"/> vivo</b> (no soft-deleted) de la reserva: mismo criterio
    ///     (<c>!IsDeleted</c>) que usa el read-model de capacidades para "tiene cobros vivos".</item>
    ///   <item><b>Alguna <see cref="Invoice"/> con CAE vivo</b>: factura (no NC) con CAE asignado y
    ///     AnnulmentStatus != Succeeded — MISMO criterio que los guards de mutacion
    ///     (<c>MutationGuards</c>) usan para "factura viva". Los tipos de NC se excluyen inline porque EF Core
    ///     no traduce el helper <c>InvoiceComprobanteHelpers.IsCreditNote</c> a SQL.</item>
    /// </list>
    /// </summary>
    private async Task<bool> HasLiveMoneyAsync(int reservaId, CancellationToken ct)
    {
        // (1) Balance distinto de cero (deuda o saldo a favor), con redondeo de moneda — no decimales crudos.
        var balance = await _db.Reservas.AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => r.Balance)
            .FirstOrDefaultAsync(ct);
        if (ReservationEconomicPolicy.RoundCurrency(balance) != 0m)
            return true;

        // (2) Algun cobro vivo (real o puente) de la reserva. !IsDeleted = mismo criterio que el read-model.
        var hasLivePayment = await _db.Payments.AsNoTracking()
            .AnyAsync(p => p.ReservaId == reservaId && !p.IsDeleted, ct);
        if (hasLivePayment)
            return true;

        // (3) Alguna factura con CAE vivo (no NC). Mismo criterio que los guards de mutacion fiscal.
        var hasLiveCae = await _db.Invoices.AsNoTracking()
            .AnyAsync(i => i.ReservaId == reservaId
                && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // excluye NC (resta, no mantiene viva)
                && !string.IsNullOrEmpty(i.CAE)
                && i.AnnulmentStatus != AnnulmentStatus.Succeeded, ct);
        return hasLiveCae;
    }

    /// <summary>
    /// cbteTipo de las Notas de Credito de AFIP (3=A, 8=B, 13=C, 53=M). Espejo de
    /// <c>MutationGuards.LiveInvoiceCreditNoteTypes</c>: se excluyen del conteo de "facturas vivas" porque una
    /// NC RESTA y no debe, por si sola, frenar el cierre. Como array literal porque EF Core no traduce el helper
    /// <c>InvoiceComprobanteHelpers.IsCreditNote</c> a SQL. Mantener sincronizado con el de MutationGuards.
    /// </summary>
    private static readonly int[] LiveInvoiceCreditNoteTypes = { 3, 8, 13, 53 };

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

            // B2 (2026-06-24): el cierre por el JOB es el camino DOMINANTE (Traveling -> Closed por fin de
            // viaje). Igual que el cierre manual, al finalizar la reserva sus servicios RESUELTOS pasan a
            // "Finalizado" (prestado/cumplido). Misma FUENTE UNICA que el cierre manual
            // (ReservaServiceFinalizer): asi NINGUN camino a Closed deja servicios en "Confirmado". NO hace
            // SaveChanges: se persiste en el SaveChanges unico al final de la tanda (atomico con el cambio de
            // estado). Idempotente: re-aplicarlo sobre servicios ya finalizados es no-op.
            if (string.Equals(transition.ToStatus, EstadoReserva.Closed, StringComparison.OrdinalIgnoreCase))
            {
                await Reservations.ReservaServiceFinalizer.MarkResolvedServicesFinalizedAsync(_db, reserva.Id, ct);
            }

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
