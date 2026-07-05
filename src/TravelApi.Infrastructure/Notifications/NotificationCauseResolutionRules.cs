using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Notifications;

/// <summary>
/// (Tanda 5, 2026-07-05 — W4 del vigía de coherencia) Reglas que dicen, para un aviso persistido, si su CAUSA ya
/// murió. Es la RED DE SEGURIDAD del auto-resolve: normalmente el aviso se apaga en el acto donde la causa se
/// resuelve (factura anulada OK, cobro que salda la reserva, marca de revisión bajada). Pero si algún camino no
/// llamó a <see cref="INotificationService.ResolveByKeyAsync"/>, el aviso quedaría "zombie" (vivo sin causa). El
/// vigía nocturno pasa por acá y lo apaga.
///
/// <para><b>Diseño</b>: la DECISIÓN por aviso es una función PURA (<see cref="IsCauseResolved"/>) sobre un pequeño
/// snapshot del estado de la entidad relacionada — así es testeable sin base. El acceso a datos (cargar en bloque las
/// reservas/facturas referenciadas, sin N+1) vive en <see cref="FindZombieNotificationsAsync"/>.</para>
///
/// <para><b>Tipos cubiertos</b>:
///   - <c>ReservaNeedsReview</c> ("confirmada con cambios"): muere cuando la reserva ya no tiene cambios sin
///     reconocer (el dueño dio el OK) o desapareció.
///   - <c>ReservaUnpaidDeparture</c> ("sale pronto y debe"): muere cuando la reserva quedó saldada, pasó a un estado
///     terminal (anulada/cerrada/perdida) o desapareció.
///   - <c>Invoice</c> + error de anulación: muere cuando la factura ya quedó anulada con éxito (la NC salió).</para>
///
/// <para><b>Fuera de alcance a propósito</b>: <c>CoherenceWatchdogReport</c> (el resumen del propio vigía). Su
/// "causa" son varios hallazgos agregados sin una entidad puntual que consultar; evaluar si "ya no hay nada para
/// revisar" replicaría todo el barrido. Se deja que ese aviso lo cierre una persona (o se re-dedupe por su clave
/// estable). Ver comentario en <c>CoherenceWatchdogJob</c>.</para>
/// </summary>
public static class NotificationCauseResolutionRules
{
    // Estados de reserva donde "sale pronto y debe" ya no tiene sentido aunque el saldo figure > 0: la venta no es
    // exigible (anulada/perdida) o la reserva ya se cerró. Mismo criterio que el resto del sistema (EstadoReserva).
    private static readonly string[] UnpaidDepartureDeadStatuses =
    {
        EstadoReserva.Cancelled,
        EstadoReserva.PendingOperatorRefund,
        EstadoReserva.Closed,
        EstadoReserva.Lost,
    };

    /// <summary>Foto mínima del estado de una reserva que necesitan las reglas (sin arrastrar la entidad entera).</summary>
    public sealed record ReservaCauseState(string Status, decimal Balance, bool HasUnacknowledgedChanges);

    /// <summary>Foto mínima del estado de una factura que necesitan las reglas.</summary>
    public sealed record InvoiceCauseState(AnnulmentStatus AnnulmentStatus);

    /// <summary>True si este aviso es de un tipo que W4 sabe evaluar (tiene una regla de causa).</summary>
    public static bool IsHandled(Notification n)
    {
        if (n.RelatedEntityId is null)
            return false;

        return IsReservaNeedsReview(n)
            || IsReservaUnpaidDeparture(n)
            || IsAnnulmentError(n);
    }

    /// <summary>
    /// DECISIÓN PURA: dado un aviso y el snapshot de su entidad relacionada (reserva o factura; el que no aplique
    /// va null), devuelve true si su causa ya murió. Un snapshot null significa "la entidad ya no existe" → la causa
    /// tampoco → se considera resuelta. Un aviso que no cae en ninguna regla devuelve false (no lo tocamos).
    /// </summary>
    public static bool IsCauseResolved(Notification n, ReservaCauseState? reserva, InvoiceCauseState? invoice)
    {
        if (IsReservaNeedsReview(n))
        {
            // La marca "confirmada con cambios" vive en Reserva.HasUnacknowledgedChanges. Cuando el dueño da el OK
            // (o un estado terminal la limpia) la marca baja → el aviso ya no tiene causa.
            return reserva is null || !reserva.HasUnacknowledgedChanges;
        }

        if (IsReservaUnpaidDeparture(n))
        {
            // "Sale pronto y debe" muere si la reserva quedó saldada (Balance <= 0) o pasó a un estado donde la
            // deuda ya no es exigible (anulada/cerrada/perdida).
            return reserva is null
                || reserva.Balance <= 0m
                || UnpaidDepartureDeadStatuses.Contains(reserva.Status);
        }

        if (IsAnnulmentError(n))
        {
            // El error de anulación muere cuando la factura terminó anulada con éxito (la NC salió en un reintento).
            return invoice is null || invoice.AnnulmentStatus == AnnulmentStatus.Succeeded;
        }

        // Tipo no cubierto: no opinamos (no lo resolvemos).
        return false;
    }

    /// <summary>
    /// Barre los avisos VIVOS de tipos cubiertos, carga EN BLOQUE (sin N+1) el estado de las reservas/facturas que
    /// referencian y devuelve los que quedaron "zombie" (vivos pero con la causa ya muerta). NO muta ni guarda: el
    /// caller (W4 en CoherenceChecks) marca <see cref="Notification.ResolvedAt"/> y el job persiste.
    /// </summary>
    public static async Task<IReadOnlyList<Notification>> FindZombieNotificationsAsync(
        AppDbContext db, CancellationToken ct)
    {
        // Avisos vivos con entidad relacionada. Se filtra grueso en SQL (vivo + tiene RelatedEntityId) y fino en
        // memoria con IsHandled (que además mira el Type/Message del error de anulación, difícil de traducir a SQL).
        var liveCandidates = await db.Notifications
            .Where(n => n.ResolvedAt == null && !n.IsRead && !n.IsDismissed && n.RelatedEntityId != null)
            .ToListAsync(ct);

        var handled = liveCandidates.Where(IsHandled).ToList();
        if (handled.Count == 0)
            return Array.Empty<Notification>();

        // Ids de reservas referenciadas por avisos de reserva (needs-review o unpaid-departure).
        var reservaIds = handled
            .Where(n => IsReservaNeedsReview(n) || IsReservaUnpaidDeparture(n))
            .Select(n => n.RelatedEntityId!.Value)
            .Distinct()
            .ToList();

        // Ids de facturas referenciadas por avisos de error de anulación.
        var invoiceIds = handled
            .Where(IsAnnulmentError)
            .Select(n => n.RelatedEntityId!.Value)
            .Distinct()
            .ToList();

        var reservaStates = await db.Reservas
            .AsNoTracking()
            .Where(r => reservaIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Status, r.Balance, r.HasUnacknowledgedChanges })
            .ToDictionaryAsync(
                r => r.Id,
                r => new ReservaCauseState(r.Status, r.Balance, r.HasUnacknowledgedChanges),
                ct);

        var invoiceStates = await db.Invoices
            .AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.AnnulmentStatus })
            .ToDictionaryAsync(i => i.Id, i => new InvoiceCauseState(i.AnnulmentStatus), ct);

        var zombies = new List<Notification>();
        foreach (var notification in handled)
        {
            ct.ThrowIfCancellationRequested();

            var id = notification.RelatedEntityId!.Value;
            ReservaCauseState? reserva = reservaStates.TryGetValue(id, out var r) ? r : null;
            InvoiceCauseState? invoice = invoiceStates.TryGetValue(id, out var inv) ? inv : null;

            if (IsCauseResolved(notification, reserva, invoice))
                zombies.Add(notification);
        }

        return zombies;
    }

    // ── Identificación de cada tipo cubierto (privados, para no divergir entre IsHandled y las decisiones) ──

    private static bool IsReservaNeedsReview(Notification n)
        => n.Type == NotificationTypes.ReservaNeedsReview;

    private static bool IsReservaUnpaidDeparture(Notification n)
        => n.RelatedEntityType == NotificationRelatedEntityTypes.ReservaUnpaidDeparture;

    /// <summary>
    /// True si el aviso es un ERROR de anulación de factura. Se identifica por (Invoice + Error + el texto menciona
    /// "anul"). El match por texto es conservador a propósito: no queremos apagar un error de EMISIÓN por error.
    /// </summary>
    private static bool IsAnnulmentError(Notification n)
        => n.RelatedEntityType == NotificationRelatedEntityTypes.Invoice
           && n.Type == NotificationTypes.Error
           && n.Message.Contains("anul", StringComparison.OrdinalIgnoreCase);
}
