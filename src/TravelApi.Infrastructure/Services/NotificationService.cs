using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Notificaciones persistidas del sistema (la campanita y el banner urgente). Desde la Tanda 5 (2026-07-05) los
/// avisos tienen una CLAVE DE RESOLUCION y se apagan SOLOS cuando su causa muere (ver <see cref="Notification"/>).
///
/// <para><b>Semantica unica "vivo"</b>: un aviso se muestra mientras <c>ResolvedAt == null</c> (la causa sigue) y el
/// usuario no lo vio (<c>!IsRead &amp;&amp; !IsDismissed</c>). Antes la campanita miraba solo <c>!IsRead</c> y el banner
/// solo <c>!IsDismissed</c>, asi que descartar el banner no sacaba el punto de la campanita (y viceversa). Ahora ambas
/// vistas parten del mismo "vivo" y cada una agrega su criterio de prioridad.</para>
/// </summary>
public class NotificationService : INotificationService
{
    private readonly IRepository<Notification> _notificationRepo;
    private readonly INotificationRealtimeDispatcher _dispatcher;

    public NotificationService(
        IRepository<Notification> notificationRepo,
        INotificationRealtimeDispatcher dispatcher)
    {
        _notificationRepo = notificationRepo;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Avisos de la campanita: los VIVOS del usuario (sin resolver, sin leer, sin descartar), mas nuevos primero.
    /// </summary>
    public async Task<IEnumerable<Notification>> GetUnreadNotificationsAsync(string userId, CancellationToken ct)
    {
        return await _notificationRepo.Query()
            // "Vivo" en SQL: la causa sigue vigente (ResolvedAt == null) y el usuario no lo vio. No usamos la
            // propiedad calculada Notification.IsLive porque EF no la traduce a SQL (esta Ignore-ada en el modelo).
            .Where(n => n.UserId == userId && n.ResolvedAt == null && !n.IsRead && !n.IsDismissed)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Avisos del banner urgente: los VIVOS del usuario con prioridad Urgent. Misma base "vivo" que la campanita
    /// (antes miraba !IsDismissed, lo que lo dejaba desincronizado del punto de la campanita).
    /// </summary>
    public async Task<IEnumerable<Notification>> GetUrgentNotificationsAsync(string userId, CancellationToken ct)
    {
        return await _notificationRepo.Query()
            .Where(n => n.UserId == userId && n.Priority == "Urgent"
                        && n.ResolvedAt == null && !n.IsRead && !n.IsDismissed)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// "Ya la vi": marca el aviso como leido Y descartado a la vez. Para un unico usuario "leida" y "descartada"
    /// son la misma accion (ver nota en <see cref="Notification.IsRead"/>): asi desaparece de AMBAS vistas.
    /// </summary>
    public async Task<bool> MarkAsReadAsync(int id, string userId, CancellationToken ct)
    {
        return await MarkSeenAsync(id, userId, ct);
    }

    /// <summary>Descartar es lo mismo que marcar leida para un solo usuario: setea ambos flags. Ver <see cref="MarkAsReadAsync"/>.</summary>
    public async Task<bool> DismissAsync(int id, string userId, CancellationToken ct)
    {
        return await MarkSeenAsync(id, userId, ct);
    }

    /// <summary>Cuerpo comun de "ya la vi": valida propiedad y setea IsRead + IsDismissed. NO toca ResolvedAt
    /// (que la vea el usuario no significa que la causa haya muerto; el historico se conserva).</summary>
    private async Task<bool> MarkSeenAsync(int id, string userId, CancellationToken ct)
    {
        var notification = await _notificationRepo.GetByIdAsync(id, ct);
        if (notification == null || notification.UserId != userId)
        {
            return false;
        }

        notification.IsRead = true;
        notification.IsDismissed = true;
        await _notificationRepo.UpdateAsync(notification, ct);
        return true;
    }

    public async Task<Notification> CreateAndSendAsync(Notification notification, CancellationToken ct = default)
    {
        notification.CreatedAt = DateTime.UtcNow;
        notification.IsRead = false;
        notification.IsDismissed = false;
        notification.ResolvedAt = null;

        // TODA notificacion nace con clave de resolucion. Si el productor no la seteo explicitamente, la derivamos
        // de la convencion "{RelatedEntityType}:{RelatedEntityId}". Un productor que necesite mas granularidad
        // (varios avisos distintos sobre la misma entidad) puede setear ResolutionKey antes de llamar aca.
        if (string.IsNullOrWhiteSpace(notification.ResolutionKey))
        {
            notification.ResolutionKey =
                NotificationResolutionKeys.ForEntity(notification.RelatedEntityType, notification.RelatedEntityId);
        }

        await _notificationRepo.AddAsync(notification, ct);
        await _dispatcher.DispatchAsync(notification, ct);

        return notification;
    }

    public async Task<int> ResolveByKeyAsync(string? resolutionKey, CancellationToken ct = default)
    {
        // Clave vacia = no hay causa identificable que apagar. No-op (idempotente).
        if (string.IsNullOrWhiteSpace(resolutionKey))
            return 0;

        // Solo los VIVOS con esa clave: si ya estaban resueltos, leidos o descartados no hay nada que apagar.
        // Cargamos TRACKED (los vamos a mutar) via el repo, y guardamos una sola vez. No usamos ExecuteUpdate:
        // el provider InMemory de los tests no lo soporta.
        var live = await _notificationRepo.Query()
            .Where(n => n.ResolutionKey == resolutionKey && n.ResolvedAt == null && !n.IsRead && !n.IsDismissed)
            .ToListAsync(ct);

        if (live.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var notification in live)
        {
            notification.ResolvedAt = now;
        }

        // UpdateAsync guarda; con el primero alcanza (el resto ya quedaron trackeados como modificados en el
        // mismo contexto, un unico SaveChanges los persiste a todos).
        await _notificationRepo.UpdateAsync(live[0], ct);

        return live.Count;
    }
}
