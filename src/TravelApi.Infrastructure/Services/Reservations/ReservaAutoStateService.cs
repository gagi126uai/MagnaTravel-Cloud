using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// ADR-020 F3 (motor de estados automatico). UNICO responsable de las transiciones
/// InManagement &lt;-&gt; Confirmed (INV-020-02: jamas se hacen a mano). Se invoca despues de cada
/// mutacion de servicio, como un SaveChanges SEPARADO inmediatamente posterior al de la mutacion
/// (mismo patron que el recalculo de saldo). NO es un job: el dueño quiere que la reserva pase a
/// Confirmada "al resolverse el ultimo servicio".
///
/// <para>Hace DOS cosas en cada evaluacion (idempotente: evaluar dos veces seguidas no produce una
/// segunda transicion):</para>
/// <list type="number">
/// <item>Estampa <c>ConfirmedAt</c> en los servicios que acaban de pasar a confirmados por el operador.</item>
/// <item>Si TODOS los servicios vivos estan resueltos y la reserva esta En gestion -&gt; Confirmada (auto-confirm
///   hacia adelante).</item>
/// <item>Si la reserva estaba Confirmada y dejo de estar todo resuelto (o se quedo sin servicios vivos): YA NO
///   la regresa a En gestion. La deja EN Confirmed pero MARCADA "confirmada con cambios / revisar"
///   (<c>HasUnacknowledgedChanges</c>, mismo mecanismo que la edicion de precio/costo) + aviso urgente al
///   vendedor (fallback admins; sin duplicar el mismo dia). El sistema solo avisa; la persona decide. La marca
///   solo la baja una persona (endpoint acknowledge-changes) y, mientras este puesta, frena el pase automatico
///   a "En viaje" (gate en el job de lifecycle y en el pase manual).</item>
/// </list>
///
/// <para>CAMBIO DE FONDO (2026-06-24, alineado a Odoo/SAP): se elimino la regresion automatica Confirmed -&gt;
/// En gestion. Una reserva confirmada nunca vuelve sola a un estado anterior. El auto-confirm hacia adelante se
/// mantiene intacto.</para>
///
/// <para>Concurrencia: last-write-wins aceptado (el motor es idempotente por evaluacion de estado
/// total).</para>
///
/// <para><b>ADR-048 (2026-07-17, regla 9 corregida por el dueño)</b>: YA NO existe una pasada nocturna que
/// "cure" reservas desincronizadas — el estado se corrige SIEMPRE en el momento, en el mismo chokepoint
/// que mueve la plata (<c>ReservaMoneyPersister</c>, via <see cref="TravelApi.Infrastructure.Reservations.ReservaTerminalTransitionApplier"/>).
/// El job <c>ReservaLifecycleAutomationService</c> sigue existiendo (fuera de alcance eliminarlo) y de
/// hecho tambien corre esta misma derivacion, pero es una red de seguridad incidental, NO el mecanismo de
/// diseño: si un cambio toca la reserva, el estado sale correcto ahi mismo, sin esperar a la noche.</para>
/// </summary>
public class ReservaAutoStateService
{
    // Actores "sistema" para el rastro auditable cuando la transicion la dispara el motor.
    private const string SystemActorUserId = "system:auto-state";
    private const string SystemActorUserName = "Sistema (motor de estados)";

    private readonly AppDbContext _context;
    private readonly ILogger<ReservaAutoStateService> _logger;

    public ReservaAutoStateService(AppDbContext context, ILogger<ReservaAutoStateService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Evalua una reserva y aplica (con su propio SaveChanges) el estampado de ConfirmedAt y la
    /// transicion automatica que corresponda. Devuelve <c>true</c> SOLO si hubo un cambio de ESTADO real (hoy
    /// el unico es el auto-confirm InManagement -&gt; Confirmed) — lo usa la reconciliacion nocturna para contar
    /// cuantas curo. Marcar una reserva "confirmada con cambios" NO cuenta como cambio de estado (no la cura,
    /// solo la marca), asi que no infla ese contador.
    ///
    /// <para><paramref name="suppressNotifications"/>: cuando es true NO se crea el aviso de "confirmada con
    /// cambios / revisar". Lo usa la reconciliacion nocturna: es una CURA en lote, no un evento en vivo, y su
    /// PRIMERA corrida tras el deploy marcaria en masa reservas historicas Confirmed que con las reglas nuevas
    /// tienen servicios sin resolver — un aviso por cada una seria ruido. La marca (HasUnacknowledgedChanges +
    /// el texto del motivo) SI se setea igual, solo se calla la campana.</para>
    ///
    /// <para><paramref name="skipTerminalDerivation"/> (ADR-048, M1): cuando es true, este metodo NO vuelve a
    /// evaluar "tuvo servicios y todos anulados" (esa evaluacion ya corrio, en la MISMA transaccion, dentro de
    /// <c>ReservaMoneyPersister</c> — via atomica B2). Lo usa <c>ReservaService.UpdateBalanceAsync</c> (que
    /// SIEMPRE pasa por el persister antes de llamar aca) para no correr el motor dos veces. El resto de los
    /// callers (ej. la reconciliacion nocturna, que NO pasa por el persister) lo dejan en el default
    /// (<c>false</c>) para seguir cubriendo esa derivacion.</para>
    /// </summary>
    public async Task<bool> EvaluateAndApplyAsync(
        int reservaId,
        bool suppressNotifications = false,
        CancellationToken ct = default,
        bool skipTerminalDerivation = false)
    {
        var reserva = await _context.Reservas
            .Include(r => r.FlightSegments)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .Include(r => r.Servicios)
            .FirstOrDefaultAsync(r => r.Id == reservaId, ct);
        if (reserva == null) return false;

        var now = DateTime.UtcNow;
        bool anyChange = StampConfirmedAt(reserva, now);
        // Marca "confirmada con cambios" recien puesta en esta evaluacion: hay que persistirla y avisar, pero
        // NO es un cambio de ESTADO, asi que no infla el contador de "reservas curadas" de la reconciliacion.
        bool needsReviewMarked = false;

        // El motor solo opera en la frontera InManagement <-> Confirmed. En cualquier otro estado
        // (Quotation, Budget, Traveling, Closed, Lost, Cancelled...) no toca el estado. (ADR-036: ToSettle murio.)
        bool isEngineState =
            string.Equals(reserva.Status, EstadoReserva.InManagement, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reserva.Status, EstadoReserva.Confirmed, StringComparison.OrdinalIgnoreCase);

        if (isEngineState)
        {
            // ADR-048 (INV-048-01, B1): si la reserva TUVO servicios y quedaron TODOS anulados, el terminal
            // del par (Anulada / Esperando reembolso del operador — se decide a NIVEL RESERVA con todas sus
            // cancelaciones) manda por sobre cualquier otra rama de abajo: no hay nada vivo que auto-confirmar
            // ni que marcar "para revisar". Cubre TANTO InManagement como Confirmed (antes de este cambio,
            // una reserva En gestion que se quedaba sin servicios no transicionaba a ningun lado).
            bool terminalTransitioned = !skipTerminalDerivation
                && await TravelApi.Infrastructure.Reservations.ReservaTerminalTransitionApplier
                    .ApplyIfNeededAsync(_context, reserva, now, ct);

            bool allResolved = AllLiveServicesResolved(reserva);

            if (terminalTransitioned)
            {
                anyChange = true;
            }
            else if (string.Equals(reserva.Status, EstadoReserva.InManagement, StringComparison.OrdinalIgnoreCase) && allResolved)
            {
                await ApplyTransitionAsync(reserva, EstadoReserva.Confirmed, "Forward", now,
                    "Todos los servicios resueltos: confirmacion automatica", ct);
                anyChange = true;
                // OJO: NO se limpia aca la marca "confirmada con cambios" (HasUnacknowledgedChanges).
                // Esa marca representa "el dueño todavia no reviso un cambio" y solo la baja una
                // PERSONA (endpoint acknowledge-changes). Que los servicios se vuelvan a resolver solos no
                // significa que el dueño ya vio lo que paso; el aviso queda hasta que de el OK.
                // Lo que SI limpia el transicionador al entrar a Confirmed es LastRegression* (el motivo
                // de una regresion anterior ya cumplio su ciclo) — regla declarativa en ReservaStateCleanupRules.
            }
            else if (string.Equals(reserva.Status, EstadoReserva.Confirmed, StringComparison.OrdinalIgnoreCase) && !allResolved)
            {
                // CAMBIO DE FONDO (2026-06-24, alineado a Odoo/SAP): una reserva confirmada YA NO vuelve sola a
                // "En gestion" cuando un servicio deja de estar resuelto (el operador cancelo/reprogramo, se
                // agrego un servicio nuevo). Regresarla de estado era un movimiento automatico sorpresivo que
                // pisaba lo que el usuario veia. En su lugar la dejamos EN Confirmed pero MARCADA "confirmada
                // con cambios / revisar" (mismo mecanismo que usa la edicion de precio/costo, ADR-027): el
                // sistema solo AVISA, la persona decide que hacer. NOTA (ADR-048): el subcaso "se quedo sin
                // servicios vivos" YA NO llega aca — lo captura la rama del terminal de arriba.
                //
                // CRITICO (cobertura del hueco): mientras la marca este puesta, el gate de pase a "En viaje"
                // (job nocturno y pase manual) NO promueve la reserva. Antes la regresion cumplia ese rol
                // (al no estar en Confirmed, el job no la tomaba); ahora ese candado lo da la marca.
                bool marked = MarkNeedsReview(reserva, now);
                if (marked)
                {
                    needsReviewMarked = true;
                    if (!suppressNotifications)
                        await NotifyNeedsReviewAsync(reserva, now, ct);
                }
            }
        }

        // CRM leads (fix de fondo 2026-06-18): el motor es el chokepoint de TODA entrada automatica a un
        // estado firme — la auto-confirmacion en vivo (InManagement -> Confirmed, disparada desde
        // ReservaService.UpdateBalanceAsync) y la reconciliacion nocturna del job pasan ambas por aca. Si la
        // reserva quedo en venta operativa viva y nacio de un lead, ese lead pasa a Ganado. Se evalua SIEMPRE
        // (no solo en la rama Forward) porque una reserva ya Confirmed/Traveling que no transiciono igual debe
        // tener su lead Ganado; el hook es idempotente (no re-sella un lead ya Ganado/Perdido). Va ANTES del
        // SaveChanges para persistir el lead en la misma transaccion que el cambio del motor (todo o nada).
        bool leadMarkedWon = await SourceLeadWonHook.MarkSourceLeadAsWonIfReservaIsFirmAsync(_context, reserva, ct);

        // Persistimos si el motor cambio el estado, O si acaba de marcar la reserva "con cambios para revisar",
        // O si el hook marco el lead como Ganado. Usamos bools precisos (no ChangeTracker.HasChanges() global)
        // para no flushear cambios ajenos que el contexto compartido pudiera tener pendientes en los flujos de plata.
        if (anyChange || needsReviewMarked || leadMarkedWon)
            await _context.SaveChangesAsync(ct);

        // El return sigue siendo "hubo cambio de ESTADO" (lo que cuenta la reconciliacion como reserva curada):
        // marcar un lead no es curar un estado, asi que no infla ese contador.
        return anyChange;
    }

    /// <summary>
    /// Estampa <c>ConfirmedAt = now</c> en cada servicio que esta confirmado por el operador y aun no
    /// tenia la marca. NO la borra al des-confirmar (queda como historia; se re-estampa al re-confirmar).
    /// Devuelve true si estampo alguno.
    /// </summary>
    private static bool StampConfirmedAt(Reserva reserva, DateTime now)
    {
        bool changed = false;

        foreach (var f in reserva.FlightSegments)
            if (f.ConfirmedAt == null && ServiceResolutionRules.IsOperatorConfirmed(f)) { f.ConfirmedAt = now; changed = true; }
        foreach (var h in reserva.HotelBookings)
            if (h.ConfirmedAt == null && ServiceResolutionRules.IsOperatorConfirmed(h)) { h.ConfirmedAt = now; changed = true; }
        foreach (var t in reserva.TransferBookings)
            if (t.ConfirmedAt == null && ServiceResolutionRules.IsOperatorConfirmed(t)) { t.ConfirmedAt = now; changed = true; }
        foreach (var p in reserva.PackageBookings)
            if (p.ConfirmedAt == null && ServiceResolutionRules.IsOperatorConfirmed(p)) { p.ConfirmedAt = now; changed = true; }
        foreach (var a in reserva.AssistanceBookings)
            if (a.ConfirmedAt == null && ServiceResolutionRules.IsOperatorConfirmed(a)) { a.ConfirmedAt = now; changed = true; }
        foreach (var s in reserva.Servicios)
            if (s.ConfirmedAt == null && ServiceResolutionRules.IsOperatorConfirmed(s)) { s.ConfirmedAt = now; changed = true; }

        return changed;
    }

    /// <summary>
    /// "Todos los servicios vivos (no cancelados) estan resueltos" — y hay al menos uno. Es la
    /// condicion para que la reserva pase (o se mantenga en) Confirmada.
    /// </summary>
    private static bool AllLiveServicesResolved(Reserva reserva)
    {
        int liveCount = 0;
        bool allResolved = true;

        void Check<T>(IEnumerable<T> items, Func<T, bool> isCancelled, Func<T, bool> isResolved)
        {
            foreach (var item in items)
            {
                if (isCancelled(item)) continue; // los cancelados no cuentan ni bloquean
                liveCount++;
                if (!isResolved(item)) allResolved = false;
            }
        }

        Check(reserva.FlightSegments, ServiceResolutionRules.IsCancelled, ServiceResolutionRules.IsResolved);
        Check(reserva.HotelBookings, ServiceResolutionRules.IsCancelled, ServiceResolutionRules.IsResolved);
        Check(reserva.TransferBookings, ServiceResolutionRules.IsCancelled, ServiceResolutionRules.IsResolved);
        Check(reserva.PackageBookings, ServiceResolutionRules.IsCancelled, ServiceResolutionRules.IsResolved);
        Check(reserva.AssistanceBookings, ServiceResolutionRules.IsCancelled, ServiceResolutionRules.IsResolved);
        Check(reserva.Servicios, ServiceResolutionRules.IsCancelled, ServiceResolutionRules.IsResolved);

        return liveCount >= 1 && allResolved;
    }

    /// <summary>
    /// Deja la reserva confirmada MARCADA "confirmada con cambios / revisar" (sin cambiar su estado) cuando un
    /// servicio dejo de estar resuelto o se quedo sin servicios vivos. Reusa el mismo mecanismo que la edicion
    /// de precio/costo (ADR-027): <c>HasUnacknowledgedChanges</c> + <c>ChangesPendingSince</c>. La marca solo la
    /// baja una PERSONA (endpoint acknowledge-changes), nunca se limpia sola.
    ///
    /// <para>Devuelve <c>true</c> si esta evaluacion ACABA de poner la marca (no estaba puesta). Si ya estaba
    /// marcada, refresca solo el texto del motivo (por si cambio que servicio quedo sin resolver) y devuelve
    /// <c>false</c> — asi no se re-dispara el aviso ni se re-pisa la fecha "desde cuando esta pendiente".</para>
    ///
    /// <para><c>ChangesPendingSince</c> NO se re-pisa si ya habia algo pendiente (idempotente, mismo criterio
    /// que <c>MarkUnacknowledgedChangesIfLiveAsync</c> en ReservaService): representa "desde cuando hay algo
    /// para revisar".</para>
    /// </summary>
    private bool MarkNeedsReview(Reserva reserva, DateTime now)
    {
        var reason = BuildNeedsReviewReason(reserva);

        // Texto del motivo: se guarda en LastRegressionReason/LastRegressionAt, los campos que el frontend ya
        // lee para mostrar la franja informativa. Tras sacar la regresion, estos campos pasaron a significar
        // "motivo por el que hay que revisar la reserva" (ya no "volvio a En gestion"). Se refresca siempre
        // (puede haber cambiado que servicio quedo sin resolver). Lo limpia el acknowledge junto con la marca.
        reserva.LastRegressionReason = reason;
        reserva.LastRegressionAt = now;

        // Si ya estaba marcada, no es una marca NUEVA: no re-disparamos aviso ni re-pisamos la fecha.
        if (reserva.HasUnacknowledgedChanges)
            return false;

        reserva.HasUnacknowledgedChanges = true;
        reserva.ChangesPendingSince = now;

        _logger.LogInformation(
            "Auto-state: Reserva {ReservaId} queda Confirmada pero marcada 'confirmada con cambios' (un servicio dejo de estar resuelto o se quedo sin servicios). Motivo: {Reason}",
            reserva.Id, reason);

        return true;
    }

    /// <summary>
    /// Arma el texto del motivo de revision. Intenta nombrar el/los tipo(s) de servicio que dejaron de estar
    /// resueltos (lo mas accionable que podemos derivar sin input humano). Si la reserva quedo sin servicios
    /// vivos, lo dice claro. Si no logra identificar ninguno (caso raro), cae a un mensaje neutro. Sin datos
    /// sensibles (ni montos ni pasajeros).
    /// </summary>
    private static string BuildNeedsReviewReason(Reserva reserva)
    {
        // Reusa la fuente unica de "que servicios vivos siguen sin resolver" (misma que usa el voucher).
        var unresolvedTypes = ServiceResolutionRules.GetUnresolvedLiveServiceLabels(reserva);

        if (unresolvedTypes.Count > 0)
            return $"Hay servicios que dejaron de estar resueltos: {string.Join(", ", unresolvedTypes)}. " +
                   "Puede ser un servicio nuevo o que el operador cancelo/reprogramo uno confirmado. Revisala.";

        // La lista de "sin resolver" puede venir VACIA cuando la reserva quedo SIN NINGUN servicio vivo (se
        // cancelaron o eliminaron todos). En ese caso ningun servicio "dejo de estar resuelto" (no hay
        // servicios), asi que lo decimos como lo que es, no con el mensaje de servicios pendientes.
        bool hasAnyLiveService = HasAnyLiveService(reserva);
        if (!hasAnyLiveService)
            return "La reserva quedo sin servicios activos (se cancelaron o eliminaron todos). " +
                   "Agrega al menos un servicio o revisa la reserva.";

        // Caso teorico residual (hay servicios vivos pero no se pudo nombrar ninguno como sin resolver).
        return "La reserva tiene cambios para revisar antes de avanzar al viaje.";
    }

    /// <summary>
    /// True si la reserva tiene AL MENOS un servicio vivo (no cancelado) en cualquiera de sus 6 colecciones.
    /// Mismo criterio de "vivo" que <see cref="AllLiveServicesResolved"/> (usa los predicados IsCancelled),
    /// para no divergir: si este metodo dice "no hay vivos", el motor tambien conto liveCount = 0.
    /// </summary>
    private static bool HasAnyLiveService(Reserva reserva)
    {
        bool AnyLive<T>(IEnumerable<T> items, Func<T, bool> isCancelled)
            => items.Any(item => !isCancelled(item));

        return AnyLive(reserva.FlightSegments, ServiceResolutionRules.IsCancelled)
            || AnyLive(reserva.HotelBookings, ServiceResolutionRules.IsCancelled)
            || AnyLive(reserva.TransferBookings, ServiceResolutionRules.IsCancelled)
            || AnyLive(reserva.PackageBookings, ServiceResolutionRules.IsCancelled)
            || AnyLive(reserva.AssistanceBookings, ServiceResolutionRules.IsCancelled)
            || AnyLive(reserva.Servicios, ServiceResolutionRules.IsCancelled);
    }

    private async Task ApplyTransitionAsync(
        Reserva reserva, string toStatus, string direction, DateTime now, string reason, CancellationToken ct)
    {
        var fromStatus = reserva.Status;

        // Cambio de estado + rastro auditable + limpieza de marcas por el PUNTO ÚNICO de transición. La única
        // transicion que dispara este motor es InManagement -> Confirmed: su regla de limpieza toca SOLO el motivo
        // de revision (LastRegression*) y NUNCA la marca "confirmada con cambios" (ver la nota en el call-site: esa
        // marca la baja una persona con el OK). Le pasamos el `now` de la tanda para que la fila del log comparta el
        // mismo instante que el resto de la evaluacion.
        await TravelApi.Infrastructure.Reservations.ReservaStatusTransitioner.ApplyAsync(
            _context, reserva, toStatus, direction, SystemActorUserId, SystemActorUserName, reason, ct,
            occurredAt: now);

        _logger.LogInformation(
            "Auto-state: Reserva {ReservaId} {From} -> {To} ({Direction}). {Reason}",
            reserva.Id, fromStatus, toStatus, direction, reason);
    }

    /// <summary>
    /// Avisa al vendedor responsable que la reserva quedo "confirmada con cambios" y hay que revisarla
    /// (Priority=Urgent), SIN haberla regresado de estado. Si la reserva no tiene responsable
    /// (ResponsibleUserId null), avisa a TODOS los admins (mejor un aviso de mas que un cambio silencioso).
    /// Dedup: NO inserta si ya hay un aviso de revision para esta reserva creado HOY (una reserva que cambia
    /// varias veces el mismo dia genera UNO).
    /// </summary>
    private async Task NotifyNeedsReviewAsync(Reserva reserva, DateTime now, CancellationToken ct)
    {
        // D5 (2026-07-05): la marca "confirmada con cambios" comparte RelatedEntityType="Reserva" con otros avisos
        // de la misma reserva, por eso su clave lleva un prefijo dedicado ("ReservaNeedsReview:{id}"): asi el
        // auto-resolutor (W4) y el dedup la distinguen de "sale pronto y debe". El aviso se apaga solo cuando la
        // reserva deja de tener cambios sin reconocer (ver NotificationCauseResolutionRules).
        var resolutionKey = NotificationResolutionKeys.ForTyped(
            NotificationTypes.ReservaNeedsReview, reserva.Id);

        // Dedup por AVISO VIVO (no por "creado hoy"): no re-avisar mientras siga vivo un aviso con esta clave para
        // algun destinatario. Si el dueno ya lo vio (leido/descartado) o se resolvio, un cambio nuevo puede volver
        // a avisar. Chequeo global por clave (no por usuario) para no duplicar cuando el mismo evento avisa a varios.
        bool hasLiveAlert = await _context.Notifications.AnyAsync(n =>
            n.ResolutionKey == resolutionKey
            && n.ResolvedAt == null && !n.IsRead && !n.IsDismissed, ct);
        if (hasLiveAlert) return;

        var message =
            $"Revisá la reserva {reserva.NumeroReserva}: cambió algo de los servicios (el operador movió algo o " +
            "se agregó/quitó un servicio). Sigue confirmada, pero chequeala antes de que salga el viaje.";

        var recipients = new List<string>();
        if (!string.IsNullOrEmpty(reserva.ResponsibleUserId))
        {
            recipients.Add(reserva.ResponsibleUserId);
        }
        else
        {
            // Fallback: avisar a todos los admins.
            var adminUserIds = await (
                from userRole in _context.UserRoles
                join role in _context.Roles on userRole.RoleId equals role.Id
                where role.Name == "Admin"
                select userRole.UserId).Distinct().ToListAsync(ct);
            recipients.AddRange(adminUserIds);
        }

        foreach (var userId in recipients)
        {
            _context.Notifications.Add(new Notification
            {
                UserId = userId,
                Message = message,
                Type = NotificationTypes.ReservaNeedsReview,
                Priority = "Urgent",
                RelatedEntityId = reserva.Id,
                RelatedEntityType = NotificationRelatedEntityTypes.Reserva,
                ResolutionKey = resolutionKey,
                CreatedAt = now
            });
        }
    }
}
