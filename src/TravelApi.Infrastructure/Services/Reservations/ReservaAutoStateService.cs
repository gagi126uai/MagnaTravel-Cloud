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
/// <item>Si TODOS los servicios vivos estan resueltos y la reserva esta En gestion -&gt; Confirmada.
///   Si la reserva estaba Confirmada y dejo de estar todo resuelto -&gt; vuelve a En gestion + aviso
///   urgente al vendedor (fallback admins si no hay responsable; sin duplicar el mismo dia).</item>
/// </list>
///
/// <para>Concurrencia: last-write-wins aceptado (el motor es idempotente por evaluacion de estado
/// total). La reconciliacion nocturna del job cura cualquier reserva que haya esquivado el chokepoint.</para>
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
    /// transicion automatica que corresponda. Devuelve <c>true</c> si hubo un cambio de estado real
    /// (forward o regresion) — lo usa la reconciliacion nocturna para contar cuantas curo.
    ///
    /// <para><paramref name="suppressNotifications"/>: cuando es true NO se crea el aviso de regresion.
    /// Lo usa la reconciliacion nocturna (ADR-020 §4.4): es una CURA en lote, no un evento en vivo, y
    /// su PRIMERA corrida tras el deploy regresaria en masa reservas historicas Confirmed que con las
    /// reglas nuevas tienen servicios sin resolver — un aviso por cada una seria ruido. La marca
    /// LastRegressionReason SI se setea igual (la franja naranja queda), solo se calla la campana.</para>
    /// </summary>
    public async Task<bool> EvaluateAndApplyAsync(int reservaId, bool suppressNotifications = false, CancellationToken ct = default)
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

        // El motor solo opera en la frontera InManagement <-> Confirmed. En cualquier otro estado
        // (Quotation, Budget, Traveling, ToSettle, Closed, Lost, Cancelled...) no toca el estado.
        bool isEngineState =
            string.Equals(reserva.Status, EstadoReserva.InManagement, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reserva.Status, EstadoReserva.Confirmed, StringComparison.OrdinalIgnoreCase);

        if (isEngineState)
        {
            bool allResolved = AllLiveServicesResolved(reserva);

            if (string.Equals(reserva.Status, EstadoReserva.InManagement, StringComparison.OrdinalIgnoreCase) && allResolved)
            {
                ApplyTransition(reserva, EstadoReserva.Confirmed, "Forward", now,
                    "Todos los servicios resueltos: confirmacion automatica");
                // La reserva volvio a estar OK: la franja naranja de regresion ya no aplica.
                reserva.LastRegressionReason = null;
                reserva.LastRegressionAt = null;
                anyChange = true;
            }
            else if (string.Equals(reserva.Status, EstadoReserva.Confirmed, StringComparison.OrdinalIgnoreCase) && !allResolved)
            {
                var regressionReason = BuildRegressionReason(reserva);
                ApplyTransition(reserva, EstadoReserva.InManagement, "Revert", now, regressionReason);
                // Decision #6: el frontend muestra una franja naranja con este motivo hasta que la
                // reserva se vuelva a auto-confirmar (ahi se limpia, ver rama Forward).
                reserva.LastRegressionReason = regressionReason;
                reserva.LastRegressionAt = now;
                if (!suppressNotifications)
                    await NotifyRegressionAsync(reserva, now, ct);
                anyChange = true;
            }
        }

        if (anyChange)
            await _context.SaveChangesAsync(ct);

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
    /// ADR-020 (decision #6): arma el texto de la franja naranja. Intenta nombrar el/los tipo(s) de
    /// servicio que dejaron de estar resueltos (lo mas accionable que podemos derivar sin input humano).
    /// Si no logra identificar ninguno (caso raro), cae a un mensaje generico.
    /// </summary>
    private static string BuildRegressionReason(Reserva reserva)
    {
        // Reusa la fuente unica de "que servicios vivos siguen sin resolver" (misma que usa el voucher).
        var unresolvedTypes = ServiceResolutionRules.GetUnresolvedLiveServiceLabels(reserva);

        if (unresolvedTypes.Count == 0)
            return "Un servicio dejo de estar resuelto: regresion automatica a En gestion";

        return $"Volvio a En gestion porque hay servicios sin resolver: {string.Join(", ", unresolvedTypes)}. " +
               "Puede ser un servicio nuevo o que el operador cancelo/reprogramo uno confirmado.";
    }

    private void ApplyTransition(Reserva reserva, string toStatus, string direction, DateTime now, string reason)
    {
        var fromStatus = reserva.Status;
        reserva.Status = toStatus;
        _context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = reserva.Id,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            Direction = direction,
            ByUserId = SystemActorUserId,
            ByUserName = SystemActorUserName,
            Reason = reason,
            OccurredAt = now
        });
        _logger.LogInformation(
            "Auto-state: Reserva {ReservaId} {From} -> {To} ({Direction}). {Reason}",
            reserva.Id, fromStatus, toStatus, direction, reason);
    }

    /// <summary>
    /// Avisa al vendedor responsable que la reserva regreso a En gestion (Priority=Urgent). Si la
    /// reserva no tiene responsable (ResponsibleUserId null), avisa a TODOS los admins (mejor un aviso
    /// de mas que una regresion silenciosa). Dedup: NO inserta si ya hay un aviso de regresion para esta
    /// reserva creado HOY (una reserva que entra y sale de Confirmada varias veces el mismo dia genera UNO).
    /// </summary>
    private async Task NotifyRegressionAsync(Reserva reserva, DateTime now, CancellationToken ct)
    {
        var today = now.Date;
        var tomorrow = today.AddDays(1);

        // Dedup por Type DEDICADO (ADR-020): antes el filtro era Type=Warning+Priority=Urgent, demasiado
        // amplio — un aviso urgente cualquiera de la misma reserva podia suprimir la regresion (o al
        // reves). Con el Type propio el dedup matchea SOLO regresiones.
        bool alreadyNotifiedToday = await _context.Notifications.AnyAsync(n =>
            n.RelatedEntityType == "Reserva"
            && n.RelatedEntityId == reserva.Id
            && n.Type == NotificationTypes.ReservaAutoRegression
            && n.CreatedAt >= today && n.CreatedAt < tomorrow, ct);
        if (alreadyNotifiedToday) return;

        var message =
            $"La reserva {reserva.NumeroReserva} volvio a 'En gestion': un servicio dejo de estar resuelto " +
            "(el operador cancelo/reprogramo o se agrego un servicio nuevo). Revisala.";

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
                Type = NotificationTypes.ReservaAutoRegression,
                Priority = "Urgent",
                RelatedEntityId = reserva.Id,
                RelatedEntityType = "Reserva",
                CreatedAt = now
            });
        }
    }
}
