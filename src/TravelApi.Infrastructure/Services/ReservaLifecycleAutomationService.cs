using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;

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

    // ADR-040: diccionarios vacios reutilizables para clientes a cuenta sin limites/sin exposicion cargada. Sin
    // limites = todas las monedas en las que deba son prepago (la politica las bloquea); sin exposicion = no debe.
    private static readonly IReadOnlyDictionary<string, decimal> EmptyLimits =
        new Dictionary<string, decimal>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, decimal> EmptyExposure =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

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

    /// <summary>
    /// Entry point que invoca Hangfire en el cron de las 3am. Delega en <see cref="RunDailyDetailedAsync"/> y
    /// devuelve el total de transiciones aplicadas.
    ///
    /// <para>ARREGLO 2 (2026-06-25): <c>[DisableConcurrentExecution]</c> es el guard de Hangfire contra corridas
    /// PROGRAMADAS solapadas. Sin el, una corrida nocturna lenta que se cruzara con la del dia siguiente podia
    /// pisarse en la ventana entre la re-lectura y el SaveChanges -> auditoria duplicada y doble procesamiento.
    /// Hangfire toma un lock distribuido (tabla de locks de Postgres) keyado por este metodo: la segunda corrida
    /// ESPERA hasta el timeout a que termine la primera; si no lo consigue, falla esa ejecucion (no corre en
    /// paralelo). El timeout (10 min) cubre una corrida nocturna larga sin ser eterno.</para>
    ///
    /// <para>El disparo MANUAL del admin (<c>AdminMaintenanceController.RunLifecycle</c>) corre INLINE
    /// <see cref="RunDailyDetailedAsync"/> y NO comparte este lock (mantiene su contrato sincrono). Una eventual
    /// superposicion manual+programada es SEGURA a nivel datos gracias al ARREGLO 1 (cada transicion re-valida
    /// estado y saldo frescos antes de aplicar): la segunda corrida ve los estados ya movidos y los saltea. El
    /// peor efecto seria trabajo repetido, no corrupcion. Ver la nota en el controller para cerrar esa ventana.</para>
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task<int> RunDailyAsync(CancellationToken ct = default)
    {
        var result = await RunDailyDetailedAsync(ct);
        return result.Promoted + result.Closed + result.Repaired + result.Reconciled + result.Expired;
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
        // ARREGLO 3 (2026-06-25): cada fase va aislada en su propio try/catch (via RunPhaseSafelyAsync). Antes,
        // una sola fila veneno (FK, constraint, deadlock) que reventara una fase abortaba la corrida ENTERA y las
        // fases SIGUIENTES nunca corrian esa noche (ej. fallaba "Confirmada->En viaje" y nunca corria el cierre
        // por fin de viaje). Ahora una fase que explota se loguea y devuelve 0, y la corrida sigue con la
        // siguiente. La atomicidad DENTRO de cada transicion individual se mantiene (ver ApplyTransitionsAsync).

        // ADR-020 F3 (cura del motor): re-evalua el motor sobre todas las reservas En gestion /
        // Confirmada y corrige las que hayan esquivado el chokepoint o quedado en la ventana entre
        // los dos saves. Corre PRIMERO para que el resto del job vea estados ya reconciliados.
        var reconciled = await RunPhaseSafelyAsync("ReconcileAutoStates", () => ReconcileAutoStatesAsync(ct));

        var repaired = await RunPhaseSafelyAsync("AutoRepairTravelingDates", () => AutoRepairTravelingDatesAsync(ct));

        // ADR-036 (saneamiento): cerrar las reservas que ya quedaron En viaje SIN servicios. Corre junto al
        // resto del housekeeping de Traveling y antes del cierre por fin de viaje (una vacia no tiene EndDate,
        // asi que el cierre normal no la alcanzaria; este barrido la cierra por estar vacia).
        var emptyTravelingClosed = await RunPhaseSafelyAsync("AutoCloseEmptyTraveling", () => AutoCloseEmptyTravelingAsync(ct));

        // G6 (caducidad de pre-venta, 2026-06-24): un Presupuesto/Cotizacion que no avanzo en X dias caduca
        // SOLO a "Perdido". Corre junto al resto del housekeeping. Es independiente del flujo de viaje, por eso
        // su count NO se suma a Promoted/Closed (se loguea aparte). Si los dias estan en 0 (default), no hace nada.
        var expired = await RunPhaseSafelyAsync("AutoExpireStalePreSale", () => AutoExpireStalePreSaleAsync(ct));

        var promoted = await RunPhaseSafelyAsync("AutoTransitionConfirmedToTraveling", () => AutoTransitionConfirmedToTravelingAsync(ct));

        // ADR-020: cierre por fin de viaje Traveling -> Closed (EndDate pasada + Balance <= 0). El
        // <= 0 (no == 0) cubre el saldo a favor: una reserva cuyo cliente pago de mas debe poder
        // cerrarse igual (coherente con el gate manual, que solo bloquea Balance > 0). ADR-036: ToSettle murio.
        var closed = await RunPhaseSafelyAsync("AutoTransitionTravelingToClosed", () => AutoTransitionTravelingToClosedAsync(ct));

        _logger.LogInformation(
            "Lifecycle automation finished. Reconciled (motor): {Reconciled}. Repaired: {Repaired}. En viaje vacias cerradas (saneamiento): {EmptyTravelingClosed}. Pre-venta caducada a Perdido: {Expired}. Confirmed->Traveling: {Promoted}. Avanzadas a fin de viaje: {Closed}.",
            reconciled, repaired, emptyTravelingClosed, expired, promoted, closed);
        // El saneamiento cuenta como cierres: se suma al count Closed del resultado. La caducidad de pre-venta
        // (G6) va en su propio campo Expired (no es ni promote ni close: es Budget/Quotation -> Lost).
        return new LifecycleRunResult(promoted, closed + emptyTravelingClosed, repaired, reconciled, expired);
    }

    /// <summary>
    /// ARREGLO 3 (2026-06-25): corre una FASE del job aislada. Si la fase lanza una excepcion (una fila veneno
    /// que ni siquiera ApplyTransitionsAsync alcanza a atajar, ej. un error en la query o en el motor de
    /// reconciliacion), la logueamos, descartamos cualquier cambio rastreado que la fase haya dejado a medias
    /// (para no contaminar la fase siguiente) y devolvemos 0. Asi una fase mala NO impide que corran las
    /// siguientes esa misma noche. La cancelacion del job (OperationCanceledException) SI se propaga: es shutdown,
    /// no una fila veneno.
    /// </summary>
    private async Task<int> RunPhaseSafelyAsync(string phaseName, Func<Task<int>> phase)
    {
        try
        {
            return await phase();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Lifecycle: la fase '{Phase}' fallo y se saltea entera. Las fases siguientes continuan; la proxima corrida la reintenta.",
                phaseName);
            // La fase pudo dejar entidades rastreadas a medias (ej. un SaveChanges que reviento). Las descartamos
            // para que la fase siguiente arranque limpia y no re-intente flushear esas mismas filas.
            DiscardTrackedChanges();
            return 0;
        }
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

        // ADR-040 (cuenta corriente): para bifurcar el candado de pago por modo de cobro, precargamos en una sola
        // pasada (anti N+1) los settings, los modos propios de los pagadores, sus limites por moneda y una
        // exposicion de PREVISUALIZACION. La exposicion AUTORITATIVA se re-lee FRESCA al aplicar (review B2).
        // Settings defensivo: si cae, se degrada a prepago puro y los clientes prepago promueven igual.
        var settings = await LoadSettingsOrDefaultAsync(ct);
        var payerIds = candidates
            .Where(r => r.PayerId.HasValue)
            .Select(r => r.PayerId!.Value)
            .Distinct()
            .ToList();
        var billingModes = await ClientCreditGate.GetBillingModesAsync(_db, payerIds, ct);
        var creditLimits = await ClientCreditGate.GetLimitsByCurrencyForCustomersAsync(_db, payerIds, ct);
        var previewExposure = await CustomerCreditExposureReader.GetExposureByCurrencyForCustomersAsync(_db, payerIds, ct);

        var planned = new List<PlannedTransition>();
        var blocked = 0;

        foreach (var reserva in candidates)
        {
            // GATE "confirmada con cambios" (2026-06-24): si la reserva confirmada quedo MARCADA con cambios sin
            // revisar (un servicio dejo de estar resuelto, se quedo sin servicios, o se edito precio/costo), el
            // job NO la promueve sola a "En viaje": el dueño tiene que revisar y dar el OK primero
            // (acknowledge-changes). Antes este candado lo cumplia la regresion automatica (la reserva volvia a
            // En gestion y el job no la tomaba); al eliminar la regresion, la reserva queda en Confirmed y esta
            // marca es la que evita que progrese en silencio. Mismo gate que el pase manual
            // (EnsureCanStartTravelingAsync). Se reintenta en la proxima corrida cuando se de el OK.
            if (reserva.HasUnacknowledgedChanges)
            {
                blocked++;
                _logger.LogInformation(
                    "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Confirmed->Traveling: tiene cambios sin revisar (confirmada con cambios). Se reintenta cuando se de el OK.",
                    reserva.Id, reserva.NumeroReserva);
                continue;
            }

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

            // ADR-036/ADR-040: candado de pago del cliente, BIFURCADO por modo de cobro. Nunca excepcion en el job:
            // un cliente que no pasa el candado se cuenta como bloqueado, se loguea (sin montos) y se reintenta.
            // ResolveBillingMode trata PayerId null como Prepaid (review I1: evita NRE si el default es Account).
            var billingMode = ResolveBillingMode(reserva.PayerId, billingModes, settings.DefaultCustomerBillingMode);

            if (billingMode == CustomerBillingMode.Account)
            {
                // Cuenta corriente: puede viajar debiendo dentro de su limite por moneda. Aca evaluamos con la
                // exposicion de PREVISUALIZACION (para no planear lo que claramente no pasa); la decision
                // AUTORITATIVA la re-toma el apply con la exposicion FRESCA (review B2, ClientCreditRecheck).
                var customerId = reserva.PayerId!.Value;
                var previewDecision = ClientCreditPolicy.EvaluateCanTravel(new ClientCreditContext(
                    LimitsByCurrency: creditLimits.GetValueOrDefault(customerId) ?? EmptyLimits,
                    ExposureByCurrency: previewExposure.GetValueOrDefault(customerId) ?? EmptyExposure,
                    IsInArrears: false, // FASE 1: la mora llega en Fase 2 (vencimientos).
                    BlockWhenOverLimit: settings.BlockTravelWhenCreditExceeded,
                    ThisReservaBalance: reserva.Balance));

                if (!previewDecision.Allowed)
                {
                    blocked++;
                    _logger.LogInformation(
                        "Reserva {ReservaId} ({NumeroReserva}) NO promovida automaticamente Confirmed->Traveling: el cliente (cuenta corriente) esta fuera de su limite de credito. Se reintenta cuando regularice.",
                        reserva.Id, reserva.NumeroReserva);
                    continue;
                }

                // El cliente a cuenta NO usa el MoneyGate escalar (debe poder viajar debiendo). La re-validacion de
                // concurrencia se hace con el ClientCreditRecheck (init-only): re-lee la exposicion TOTAL fresca y
                // re-evalua la politica de credito al aplicar (review B2).
                planned.Add(new PlannedTransition(
                    Reserva: reserva,
                    FromStatus: EstadoReserva.Confirmed,
                    ToStatus: EstadoReserva.Traveling,
                    StampClosedAt: false,
                    WriteForwardLog: true,
                    Reason: "Inicio de viaje (StartDate alcanzada, cliente a cuenta corriente)",
                    MoneyGate: MoneyGate.None)
                {
                    ClientCreditRecheck = new ClientCreditRecheck(
                        CustomerId: customerId,
                        LimitsByCurrency: creditLimits.GetValueOrDefault(customerId) ?? EmptyLimits,
                        BlockWhenOverLimit: settings.BlockTravelWhenCreditExceeded,
                        IsInArrears: false)
                });
                continue;
            }

            // Prepago (ADR-036): candado DURO incondicional. Si todavia debe, no viaja. CERO cambios respecto de hoy.
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
                Reason: "Inicio de viaje (StartDate alcanzada)",
                // ARREGLO 1: re-validar al aplicar que el cliente SIGA saldado (un cobro pudo borrarse/editarse
                // entre esta query y el commit). Sin esto el job promovia a En viaje con saldo rancio.
                MoneyGate: MoneyGate.ClientFullyPaid));
        }

        var promoted = await ApplyTransitionsAsync(planned, "AutoTransitionConfirmedToTraveling", ct);

        _logger.LogInformation(
            "Auto-promoted {Promoted} Reserva(s) Confirmed->Traveling. Skipped {Blocked} por cambios sin revisar, inconsistencia de capacidad, sin servicios cargados o saldo del cliente pendiente.",
            promoted, blocked);

        return promoted;
    }

    /// <summary>
    /// AMBOS CICLOS: cierra Traveling -&gt; Closed cuando el viaje ya termino (EndDate &lt; hoy). Para clientes
    /// PREPAGO solo cierra si NO hay saldo pendiente (Balance &lt;= 0); las que tienen EndDate vencido pero saldo
    /// pendiente quedan en Traveling con chip "Vencida con deuda".
    ///
    /// <para>ADR-036 (2026-06-21, prepago puro): el fin de viaje cierra directo Traveling -&gt; Closed (ya no
    /// existe el desvio ToSettle "a liquidar").</para>
    ///
    /// <para>ADR-040 (cuenta corriente, 2026-06-26, review B4): BIFURCADO por modo de cobro. Un cliente a CUENTA
    /// CORRIENTE cierra IGUAL aunque deba — el viaje termino y su deuda sigue viva en su cuenta (AR canonico,
    /// incluye Closed). Sin esta excepcion, las reservas a cuenta con saldo quedarian atascadas "En viaje"
    /// eternamente. Para clientes a cuenta NO se usa MoneyGate (no exigimos saldo &lt;= 0 al cerrar).</para>
    /// </summary>
    public async Task<int> AutoTransitionTravelingToClosedAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;

        // Cargamos TODAS las Traveling con fin de viaje pasado (sin filtrar por saldo): el filtro de saldo pasa a
        // depender del modo de cobro. Para prepago se exige Balance <= 0 (como antes); para cuenta corriente se
        // cierra aunque deba. Resolver el modo en SQL obligaria a join + default de agencia; es mas claro y
        // mantenible resolverlo en memoria con los modos precargados (anti N+1) — el volumen es chico.
        var candidates = await _db.Reservas
            .Where(r => r.Status == EstadoReserva.Traveling
                && r.EndDate.HasValue
                && r.EndDate.Value.Date < today)
            .ToListAsync(ct);

        var settings = await LoadSettingsOrDefaultAsync(ct);
        var payerIds = candidates
            .Where(r => r.PayerId.HasValue)
            .Select(r => r.PayerId!.Value)
            .Distinct()
            .ToList();
        var billingModes = await ClientCreditGate.GetBillingModesAsync(_db, payerIds, ct);

        var planned = new List<PlannedTransition>();
        foreach (var reserva in candidates)
        {
            // ResolveBillingMode trata PayerId null como Prepaid (review I1): una reserva sin pagador no es a
            // cuenta aunque el default sea Account, asi cae al cierre prepago (exige saldo <= 0).
            var billingMode = ResolveBillingMode(reserva.PayerId, billingModes, settings.DefaultCustomerBillingMode);

            if (billingMode == CustomerBillingMode.Account)
            {
                // Cuenta corriente: cierra con deuda. SIN MoneyGate (no exigimos saldo <= 0).
                planned.Add(new PlannedTransition(
                    Reserva: reserva,
                    FromStatus: EstadoReserva.Traveling,
                    ToStatus: EstadoReserva.Closed,
                    StampClosedAt: true,
                    WriteForwardLog: true,
                    Reason: "Fin de viaje (cuenta corriente): cierre con la deuda viva en la cuenta del cliente",
                    MoneyGate: MoneyGate.None));
            }
            else if (ReservationEconomicPolicy.RoundCurrency(reserva.Balance) <= 0m)
            {
                // Prepago saldado: cierre directo (ADR-036). Prepago con deuda NO se cierra (queda "Vencida con deuda").
                planned.Add(new PlannedTransition(
                    Reserva: reserva,
                    FromStatus: EstadoReserva.Traveling,
                    ToStatus: EstadoReserva.Closed,
                    StampClosedAt: true,
                    WriteForwardLog: true,
                    Reason: "Fin de viaje (EndDate pasada) con saldo saldado: cierre directo",
                    // ARREGLO 1: re-validar al aplicar que el saldo SIGA <= 0 (un cobro pudo borrarse/editarse entre
                    // esta query y el commit, dejando deuda). Sin esto el job cerraba reservas con deuda.
                    MoneyGate: MoneyGate.BalanceNonPositive));
            }
        }

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
    /// G6 (caducidad de pre-venta, decision del dueño 2026-06-24): pasa SOLAS a "Perdido" (Lost) las reservas
    /// en Presupuesto (Budget) o Cotizacion (Quotation) que llevan mas dias en ese estado que el configurado,
    /// sin haber avanzado. Los dias se configuran por SEPARADO para cada tipo (un valor para Budget, otro para
    /// Quotation) en <see cref="OperationalFinanceSettings"/>; un valor 0 DESACTIVA la caducidad de ese tipo.
    ///
    /// <para>Reusa <see cref="ApplyTransitionsAsync"/> para heredar exactamente el mismo camino que el resto del
    /// job: rastro auditable (<see cref="ReservaStatusChangeLog"/> con actor "sistema"), re-chequeo de
    /// concurrencia (si alguien la movio a mano en el medio, se saltea) y el hook de leads. La transicion
    /// Budget->Lost y Quotation->Lost ya es legitima en <c>ReservaStatusTransitions.Forward</c>; aca solo la
    /// dispara el sistema en vez de una persona. NO duplica side-effects: G1/G2 ya garantizan que Lost no genera
    /// deuda con proveedores ni avisos.</para>
    ///
    /// <para><b>Idempotente y seguro</b>: solo toca reservas que SIGUEN en Budget/Quotation (las que avanzaron
    /// ya no estan en esos estados, asi que no se las vuelve a evaluar). El re-chequeo de concurrencia en
    /// ApplyTransitionsAsync es la red final si una reserva cambia entre la query y el save.</para>
    /// </summary>
    public async Task<int> AutoExpireStalePreSaleAsync(CancellationToken ct = default)
    {
        var settings = await _settingsService.GetEntityAsync(ct);

        var planned = new List<PlannedTransition>();

        // Budget -> Lost (si esta habilitado: dias > 0).
        if (settings.BudgetExpirationDays > 0)
        {
            await CollectExpiredPreSaleAsync(
                preSaleStatus: EstadoReserva.Budget,
                expirationDays: settings.BudgetExpirationDays,
                planned: planned,
                ct: ct);
        }

        // Quotation -> Lost (eje SEPARADO: su propio plazo, tambien gateado por dias > 0).
        if (settings.QuotationExpirationDays > 0)
        {
            await CollectExpiredPreSaleAsync(
                preSaleStatus: EstadoReserva.Quotation,
                expirationDays: settings.QuotationExpirationDays,
                planned: planned,
                ct: ct);
        }

        var expired = await ApplyTransitionsAsync(planned, "AutoExpireStalePreSale", ct);
        if (expired > 0)
        {
            _logger.LogInformation(
                "G6 caducidad de pre-venta: {Count} reserva(s) en Presupuesto/Cotizacion pasaron a Perdido por antigüedad.",
                expired);
        }

        return expired;
    }

    /// <summary>
    /// G6: junta en <paramref name="planned"/> las reservas de un estado de pre-venta concreto
    /// (<paramref name="preSaleStatus"/>) que ya superaron su plazo de caducidad, listas para pasar a Lost.
    ///
    /// <para><b>Como se mide la antigüedad</b> (decision a confirmar con el dueño): se usa el momento en que la
    /// reserva ENTRO al estado actual = el ultimo <see cref="ReservaStatusChangeLog"/> con
    /// <c>ToStatus == preSaleStatus</c>. Si no hay ningun log (caso de Quotation: la reserva NACE en Quotation
    /// y ese estado inicial no genera log de transicion), se cae a <c>CreatedAt</c>. Asi "lleva X dias sin
    /// avanzar" se mide desde que efectivamente quedo en ese estado, no desde que se creo la reserva (que para
    /// un Budget alcanzado por transicion podria ser mucho antes).</para>
    ///
    /// <para>El umbral se calcula como "entro antes de (hoy - X dias)". Se compara contra
    /// <c>DateTime.UtcNow</c> menos los dias; es una caducidad por dias corridos, coherente con el resto del
    /// job (que trabaja en UTC).</para>
    ///
    /// <para><b>GUARD DE PLATA (review de seguridad 2026-06-24)</b>: Lost es TERMINAL (no se edita, no se
    /// borra, no se devuelve el cobro). El path MANUAL de pasar a Perdida (<c>ReservaService.UpdateStatusAsync</c>)
    /// BLOQUEA Budget/Quotation -&gt; Lost si hay cobros vivos, porque el path legacy <c>AddPaymentAsync</c> no
    /// tiene gate de estado: una pre-venta PODRIA tener un cobro cargado. El job NO pasa por ese guard (va por
    /// <see cref="ApplyTransitionsAsync"/>), asi que replicamos la defensa aca: una pre-venta con un cobro vivo
    /// o una factura con CAE vivo NO se caduca (caducarla congelaria la plata huerfana sin camino de devolucion).
    /// Esas reservas se SALTEAN y se loguean (con la lista de IDs) para revision manual, mismo patron que
    /// <see cref="AutoCloseEmptyTravelingAsync"/>. Una pre-venta con deuda pero SIN cobros si caduca: es el caso
    /// normal (cotizacion impaga que el cliente no compro), no hay plata que proteger.</para>
    /// </summary>
    private async Task CollectExpiredPreSaleAsync(
        string preSaleStatus,
        int expirationDays,
        List<PlannedTransition> planned,
        CancellationToken ct)
    {
        // Frontera: cualquier reserva que entro a este estado ANTES de este instante ya caduco.
        var cutoffUtc = DateTime.UtcNow.AddDays(-expirationDays);

        // Candidatas: las que SIGUEN en el estado de pre-venta. Traemos Id + CreatedAt para el fallback.
        var candidates = await _db.Reservas
            .Where(r => r.Status == preSaleStatus)
            .Select(r => new { r.Id, r.CreatedAt, r.NumeroReserva })
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var candidateIds = candidates.Select(c => c.Id).ToList();

        // Momento en que cada candidata entro a ESTE estado = el ultimo log con ToStatus == preSaleStatus.
        // Se agrupa en memoria tras una sola query (sin N+1). Una reserva sin log para este estado no aparece
        // en el diccionario y cae al fallback CreatedAt.
        var logRows = await _db.ReservaStatusChangeLogs
            .Where(log => candidateIds.Contains(log.ReservaId) && log.ToStatus == preSaleStatus)
            .Select(log => new { log.ReservaId, log.OccurredAt })
            .ToListAsync(ct);

        var enteredStateAt = logRows
            .GroupBy(row => row.ReservaId)
            .ToDictionary(group => group.Key, group => group.Max(row => row.OccurredAt));

        // Paso 1: filtrar por antigüedad. Las que superaron el plazo son candidatas a caducar.
        var expiredIds = candidates
            .Where(candidate =>
            {
                var sinceUtc = enteredStateAt.TryGetValue(candidate.Id, out var loggedAt)
                    ? loggedAt
                    : candidate.CreatedAt; // sin log (Quotation inicial) -> desde la creacion
                return sinceUtc <= cutoffUtc;
            })
            .Select(candidate => candidate.Id)
            .ToList();

        if (expiredIds.Count == 0) return;

        // Paso 2: cargar en UNA sola query las entidades vencidas (rastreadas, para que ApplyTransitionsAsync
        // pueda mutar Status y persistir). Evita el N+1 de re-consultar una por una en el foreach.
        var expiredReservas = await _db.Reservas
            .Where(r => expiredIds.Contains(r.Id))
            .ToListAsync(ct);

        // IDs salteados por tener plata viva: se loguean al final para revision manual (NO se caducan).
        var skippedForLiveMoney = new List<(int Id, string NumeroReserva)>();

        foreach (var reserva in expiredReservas)
        {
            // GUARD DE PLATA: si tiene un cobro vivo o una factura con CAE vivo, NO se caduca (ver doc del metodo).
            if (await HasLivePaymentOrInvoiceAsync(reserva.Id, ct))
            {
                skippedForLiveMoney.Add((reserva.Id, reserva.NumeroReserva));
                continue;
            }

            planned.Add(new PlannedTransition(
                Reserva: reserva,
                FromStatus: preSaleStatus,
                ToStatus: EstadoReserva.Lost,
                StampClosedAt: false,
                WriteForwardLog: true,
                Reason: $"Caducó por antigüedad ({expirationDays} dias sin avanzar)"));
        }

        // Aviso de revision manual de las pre-ventas vencidas-con-plata: sin montos (solo Id + NumeroReserva),
        // respetando el enmascarado de costos. Mismo patron que AutoCloseEmptyTravelingAsync.
        if (skippedForLiveMoney.Count > 0)
        {
            var detalle = string.Join(", ",
                skippedForLiveMoney.Select(x => $"{x.Id} ({x.NumeroReserva})"));
            _logger.LogWarning(
                "G6 caducidad de pre-venta: {Count} reserva(s) en {PreSaleStatus} vencida(s) por antigüedad pero CON plata viva " +
                "(un cobro registrado o una factura con CAE) NO se caducaron a Perdido. Caducarlas congelaria esa plata " +
                "(Lost es terminal). Requieren revision manual: {Reservas}.",
                skippedForLiveMoney.Count, preSaleStatus, detalle);
        }
    }

    /// <summary>
    /// Guard de plata del G6 (review de seguridad 2026-06-24): true si la pre-venta tiene un cobro vivo o una
    /// factura con CAE vivo, lo que impide caducarla a Lost (terminal) sin congelar la plata.
    ///
    /// <para>A diferencia de <see cref="HasLiveMoneyAsync"/> (usado por el saneamiento de Traveling vacias),
    /// este guard NO mira el Balance: una pre-venta con deuda pero SIN cobros (cotizacion impaga) es el caso
    /// NORMAL que SI debe caducar — no hay plata recibida que proteger. Solo bloquea cuando entro plata de
    /// verdad (un Payment vivo) o se emitio un comprobante fiscal (CAE vivo). El criterio de "vivo" espeja al
    /// del guard manual (<c>!IsDeleted</c> para pagos) y al de los guards de mutacion fiscal (CAE asignado,
    /// no NC, no anulada) para no divergir.</para>
    /// </summary>
    private async Task<bool> HasLivePaymentOrInvoiceAsync(int reservaId, CancellationToken ct)
    {
        // (1) Algun cobro vivo (real o puente) de la reserva. !IsDeleted = mismo criterio que el guard manual
        // de ReservaService.UpdateStatusAsync (Quotation/Budget -> Lost) y que el read-model de capacidades.
        var hasLivePayment = await _db.Payments.AsNoTracking()
            .AnyAsync(p => p.ReservaId == reservaId && !p.IsDeleted, ct);
        if (hasLivePayment)
            return true;

        // (2) Alguna factura con CAE vivo (no NC). Mismo criterio que los guards de mutacion fiscal y que
        // HasLiveMoneyAsync. Belt-and-suspenders: una pre-venta no deberia tener CAE, pero datos legacy podrian.
        var hasLiveCae = await _db.Invoices.AsNoTracking()
            .AnyAsync(i => i.ReservaId == reservaId
                && !LiveInvoiceCreditNoteTypes.Contains(i.TipoComprobante) // excluye NC (resta, no mantiene viva)
                && !string.IsNullOrEmpty(i.CAE)
                && i.AnnulmentStatus != AnnulmentStatus.Succeeded, ct);
        return hasLiveCae;
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
    /// <para>ARREGLO 1 (saldo rancio, 2026-06-25): el re-chequeo de concurrencia re-leia SOLO el Status, nunca
    /// el Balance. Si entre la query inicial de la fase y este momento un cajero borraba/editaba un cobro (subia
    /// el saldo), el job igual promovia a "En viaje" una reserva que YA no estaba paga o cerraba una con deuda,
    /// usando el Balance viejo cargado al inicio de la fase. Ahora, ademas del Status, re-leemos el Balance
    /// FRESCO de la base y re-validamos la condicion de plata segun <see cref="PlannedTransition.MoneyGate"/>:
    /// si ya no se cumple, salteamos esa reserva (no transiciona) y lo logueamos. Nunca promovemos/cerramos con
    /// numeros rancios. El Balance es un escalar materializado (lo escribe <c>ReservaMoneyPersister</c> en cada
    /// movimiento de plata), asi que leer la fila es suficiente: no hay que recalcular aca.</para>
    ///
    /// <para>ARREGLO 3 (fila veneno, 2026-06-25): cada item va envuelto en try/catch. Si una fila falla (FK,
    /// constraint, etc.) al preparar su transicion, se SALTEA y se loguea — las demas de la tanda y las fases
    /// siguientes continuan. La atomicidad DENTRO de una transicion se mantiene: el cambio de Status + finalizar
    /// servicios + ClosedAt + log se preparan juntos y se persisten en el SaveChanges unico del final. Si una
    /// fila lanza despues de haber tocado el ChangeTracker, se descarta toda la tanda (ver el catch del
    /// SaveChanges) para no persistir un estado a medias.</para>
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

            try
            {
                // Defensa de concurrencia + saldo rancio: re-leer estado Y saldo ACTUALES de la base en una
                // sola query AsNoTracking. Si el estado cambio respecto del origen esperado, otra transaccion
                // la toco -> saltear. Si la condicion de plata ya no se cumple (un cobro se borro/edito), tampoco
                // transicionamos -> saltear sin pisar.
                var freshRow = await _db.Reservas
                    .AsNoTracking()
                    .Where(r => r.Id == reserva.Id)
                    .Select(r => new { r.Status, r.Balance })
                    .FirstOrDefaultAsync(ct);

                if (freshRow is null)
                {
                    // Defensivo: la reserva se borro entre el plan y este momento. Saltear.
                    _logger.LogWarning(
                        "Lifecycle {Operation}: Reserva {ReservaId} saltada. Ya no existe en la base (se borro entre el plan y el commit).",
                        operation, reserva.Id);
                    continue;
                }

                if (!string.Equals(freshRow.Status, transition.FromStatus, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Lifecycle {Operation}: Reserva {ReservaId} saltada. Esperaba origen '{From}' pero en la base esta en '{Current}' (otra transaccion la modifico). Se reevalua en la proxima corrida.",
                        operation, reserva.Id, transition.FromStatus, freshRow.Status);
                    continue;
                }

                // ARREGLO 1: re-validar la plata contra el saldo FRESCO (no el cargado al inicio de la fase).
                if (!MoneyGatePasses(transition.MoneyGate, freshRow.Balance))
                {
                    _logger.LogWarning(
                        "Lifecycle {Operation}: Reserva {ReservaId} saltada. El saldo cambio entre la consulta inicial y el commit y ya no cumple la condicion de plata ({Gate}). NO se transiciona con numeros rancios; se reevalua en la proxima corrida.",
                        operation, reserva.Id, transition.MoneyGate);
                    continue;
                }

                // ADR-040 (review B2): re-validacion de credito de cuenta corriente. A diferencia del MoneyGate
                // escalar (que mira UNA reserva), aca RE-LEEMOS la exposicion TOTAL FRESCA del cliente por moneda
                // y re-evaluamos la politica completa: un cobro en OTRA reserva del mismo cliente pudo cambiar su
                // situacion de credito entre el plan y el commit. Sin esto, el job promoveria a "En viaje" con una
                // exposicion rancia.
                if (transition.ClientCreditRecheck is { } recheck)
                {
                    var freshExposure = await CustomerCreditExposureReader.GetExposureByCurrencyAsync(_db, recheck.CustomerId, ct);
                    var decision = ClientCreditPolicy.EvaluateCanTravel(new ClientCreditContext(
                        LimitsByCurrency: recheck.LimitsByCurrency,
                        ExposureByCurrency: freshExposure,
                        IsInArrears: recheck.IsInArrears,
                        BlockWhenOverLimit: recheck.BlockWhenOverLimit,
                        ThisReservaBalance: freshRow.Balance));

                    if (!decision.Allowed)
                    {
                        _logger.LogWarning(
                            "Lifecycle {Operation}: Reserva {ReservaId} saltada. La exposicion de credito del cliente (cuenta corriente) cambio entre el plan y el commit y ya no cumple la politica. Se reevalua en la proxima corrida.",
                            operation, reserva.Id);
                        continue;
                    }

                    // El branch Account SIEMPRE avisa cuando hay violacion bajo llave "solo avisar".
                    if (decision.Warning != null)
                        _logger.LogWarning(
                            "Lifecycle {Operation}: Reserva {ReservaId} promovida a En viaje con AVISO de credito (cuenta corriente). {Warning}",
                            operation, reserva.Id, decision.Warning);
                }

                // Cambio de estado + rastro auditable + limpieza de marcas por el PUNTO ÚNICO de transición. Se hace
                // ANTES de StampClosedAt / Finalizer / SourceLeadWonHook para que esos pasos vean el estado nuevo.
                // stampChangeLog = transition.WriteForwardLog conserva la decisión por-transición de dejar rastro
                // (FIX 5 A1); occurredAt = now comparte el instante con el resto de la tanda. Para el cierre
                // (Traveling -> Closed) la regla de limpieza descarta la marca "confirmada con cambios" si quedara
                // colgada (en la práctica no llega marcada: el gate no promueve una reserva marcada).
                await TravelApi.Infrastructure.Reservations.ReservaStatusTransitioner.ApplyAsync(
                    _db, reserva, transition.ToStatus, "Forward",
                    SystemActorUserId, SystemActorUserName, transition.Reason, ct,
                    stampChangeLog: transition.WriteForwardLog,
                    occurredAt: now);

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

                // FIX 5 (A1): el rastro auditable (solo para transiciones de la cadena nueva) ya lo escribió el
                // PUNTO ÚNICO de transición de arriba, gobernado por stampChangeLog = transition.WriteForwardLog.

                applied++;
            }
            catch (OperationCanceledException)
            {
                // Cancelacion del job: propagar (no es una "fila veneno", es shutdown).
                throw;
            }
            catch (Exception ex)
            {
                // ARREGLO 3: una fila que falla al prepararse no debe tumbar la tanda. La salteamos y seguimos.
                // (Si llego a tocar el ChangeTracker antes de fallar, el catch del SaveChanges de abajo descarta
                // la tanda entera para no persistir a medias.)
                _logger.LogError(ex,
                    "Lifecycle {Operation}: Reserva {ReservaId} salteada por error al preparar su transicion. Las demas siguen.",
                    operation, reserva.Id);
            }
        }

        if (applied == 0) return 0;

        try
        {
            // Persistencia unica al final. Con el re-chequeo de arriba, last-write-wins solo puede
            // perderse en la ventana entre el re-read y el SaveChanges (muy chica). Aceptable: la
            // concurrencia fina es mejora futura.
            await _db.SaveChangesAsync(ct);
            return applied;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ARREGLO 3: si el SaveChanges de la tanda falla (ej. una fila veneno que solo revienta al hacer
            // flush: FK, constraint, deadlock), descartamos los cambios rastreados de ESTA fase para que la
            // proxima FASE del job pueda correr con un ChangeTracker limpio. La fase falla (devuelve 0 aplicadas)
            // pero NO propaga: el resto de la corrida nocturna continua. La proxima corrida reintenta.
            _logger.LogError(ex,
                "Lifecycle {Operation}: fallo el SaveChanges de la tanda ({Planned} planificadas). Se descartan los cambios de esta fase; las fases siguientes continuan y la proxima corrida reintenta.",
                operation, applied);
            DiscardTrackedChanges();
            return 0;
        }
    }

    /// <summary>
    /// ARREGLO 1: evalua la condicion de plata de una transicion contra un Balance FRESCO leido de la base.
    /// Es la misma regla del gate manual (<c>ReservationEconomicPolicy</c>), centralizada para no divergir.
    /// </summary>
    /// <summary>
    /// ADR-040 (review I1): resuelve el modo de cobro EFECTIVO de una reserva en el job, alineado con
    /// <c>ClientCreditGate.ResolveModeAsync</c> del path manual. Una reserva SIN pagador (<c>PayerId</c> null)
    /// es SIEMPRE Prepaid — aunque el default de agencia sea Account — porque sin cliente no hay cuenta corriente
    /// que evaluar. Esto evita el NRE de dereferenciar <c>PayerId!.Value</c> en la rama Account cuando el default
    /// es Account y la reserva no tiene pagador (landmine que tumbaba TODA la fase del job).
    /// </summary>
    private static CustomerBillingMode ResolveBillingMode(
        int? payerId,
        IReadOnlyDictionary<int, CustomerBillingMode?> billingModes,
        CustomerBillingMode agencyDefault)
        => payerId is null
            ? CustomerBillingMode.Prepaid
            : ClientBillingModeResolver.Resolve(billingModes.GetValueOrDefault(payerId.Value), agencyDefault);

    /// <summary>
    /// ADR-040: carga los settings de forma DEFENSIVA para las fases que bifurcan por modo de cobro. Si la
    /// lectura falla (ej. settings caido), NO tumba la fase: degrada a un default (prepago puro, FRENA), que es
    /// la posicion segura. Asi un problema de settings no frena la promocion/cierre de los clientes prepago (el
    /// caso comun) — solo se pierde la lenidad de cuenta corriente hasta que settings vuelva. Coherente con la
    /// resiliencia por fase (ARREGLO 3): un fallo no cascada.
    /// </summary>
    private async Task<OperationalFinanceSettings> LoadSettingsOrDefaultAsync(CancellationToken ct)
    {
        try
        {
            return await _settingsService.GetEntityAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Lifecycle: no se pudieron leer los settings; se degrada a prepago puro (FRENA) para esta fase. " +
                "Los clientes prepago siguen su candado normal; la lenidad de cuenta corriente se reanuda cuando settings vuelva.");
            return new OperationalFinanceSettings();
        }
    }

    private static bool MoneyGatePasses(MoneyGate gate, decimal freshBalance)
    {
        return gate switch
        {
            // En viaje: el cliente debe seguir saldado (mismo helper puro que el pase manual).
            MoneyGate.ClientFullyPaid => ReservationEconomicPolicy.IsClientFullyPaid(freshBalance),
            // Cierre por fin de viaje: saldo <= 0 (cubre saldo a favor), coherente con el gate manual.
            MoneyGate.BalanceNonPositive => ReservationEconomicPolicy.RoundCurrency(freshBalance) <= 0m,
            // Sin gate de plata (ej. caducidad de pre-venta G6: su propio guard ya corrio antes).
            _ => true
        };
    }

    /// <summary>
    /// ARREGLO 3: descarta TODOS los cambios rastreados (detach) tras un SaveChanges fallido de una fase, para
    /// que la fase siguiente arranque con un ChangeTracker limpio y no re-intente flushear las mismas filas
    /// veneno. Recorre una copia de las entries porque cambiar State muta la coleccion subyacente.
    /// </summary>
    private void DiscardTrackedChanges()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Re-validacion de plata que el job hace JUSTO antes de aplicar una transicion (ARREGLO 1, 2026-06-25).
    /// Indica que condicion de saldo debe RE-CHEQUEARSE contra el Balance FRESCO de la base (no el cargado al
    /// inicio de la fase), para no promover/cerrar con numeros rancios si un cajero movio la plata en el medio.
    /// </summary>
    private enum MoneyGate
    {
        /// <summary>No re-chequea plata (ej. caducidad de pre-venta G6: su guard de plata ya corrio antes).</summary>
        None,

        /// <summary>Para "En viaje": el cliente debe seguir SALDADO (IsClientFullyPaid sobre el Balance fresco).</summary>
        ClientFullyPaid,

        /// <summary>Para cerrar por fin de viaje: el saldo fresco debe seguir &lt;= 0 (coherente con el gate manual).</summary>
        BalanceNonPositive
    }

    /// <summary>
    /// Una transicion planificada por el job: la reserva a mover, su estado origen esperado
    /// (para el re-chequeo de concurrencia), el destino, si estampar ClosedAt, si debe dejar
    /// rastro auditable (solo la cadena nueva, FIX 5) y que condicion de plata re-validar contra el
    /// saldo fresco justo antes de aplicar (ARREGLO 1).
    /// </summary>
    private sealed record PlannedTransition(
        Reserva Reserva,
        string FromStatus,
        string ToStatus,
        bool StampClosedAt,
        bool WriteForwardLog,
        string? Reason,
        MoneyGate MoneyGate = MoneyGate.None)
    {
        // ADR-040 (review B2): si esta presente, al aplicar se re-lee la exposicion de credito TOTAL FRESCA del
        // cliente y se re-evalua la politica de cuenta corriente (no el saldo escalar). Solo lo usan las
        // transiciones a "En viaje" de clientes a cuenta. Null = no aplica (camino prepago / cierres). Se deja
        // FUERA del constructor posicional (propiedad init-only) a proposito: el ctor primario sigue teniendo 7
        // parametros, asi no se rompen los tests del job que construyen PlannedTransition por reflexion.
        public ClientCreditRecheck? ClientCreditRecheck { get; init; }
    }

    /// <summary>
    /// ADR-040 (review B2): datos capturados en la planificacion para RE-EVALUAR el credito del cliente a cuenta
    /// al momento de aplicar, contra la exposicion FRESCA leida en ese instante. Los limites y la llave se
    /// capturan en el plan (config que casi no cambia durante una corrida nocturna); lo que se re-lee fresco es
    /// la EXPOSICION por moneda, que es lo que un cajero puede mover entre el plan y el commit.
    /// </summary>
    private sealed record ClientCreditRecheck(
        int CustomerId,
        IReadOnlyDictionary<string, decimal> LimitsByCurrency,
        bool BlockWhenOverLimit,
        bool IsInArrears);
}

/// <summary>
/// Resultado de una corrida de lifecycle automation.
/// Repaired = cantidad de reservas Operativas con EndDate=null cuyas fechas
///            se reconstruyeron desde los servicios cargados.
/// Promoted = cantidad de reservas que pasaron Confirmed -> Traveling.
/// Closed = cantidad que el job cerro Traveling->Closed (EndDate vencido + Balance cero).
///          ADR-036: ToSettle ya no existe.
/// Expired = cantidad de pre-venta (Budget/Quotation) que el job paso a Lost por antigüedad (G6).
/// </summary>
public record LifecycleRunResult(int Promoted, int Closed, int Repaired, int Reconciled = 0, int Expired = 0);
