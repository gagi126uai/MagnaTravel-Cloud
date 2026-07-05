using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface INotificationService
{
    Task<IEnumerable<Notification>> GetUnreadNotificationsAsync(string userId, CancellationToken ct);
    Task<IEnumerable<Notification>> GetUrgentNotificationsAsync(string userId, CancellationToken ct);
    Task<bool> MarkAsReadAsync(int id, string userId, CancellationToken ct);
    Task<bool> DismissAsync(int id, string userId, CancellationToken ct);
    Task<Notification> CreateAndSendAsync(Notification notification, CancellationToken ct = default);

    /// <summary>
    /// (Tanda 5) Apaga todos los avisos VIVOS que comparten <paramref name="resolutionKey"/> marcandoles
    /// <see cref="Notification.ResolvedAt"/> = ahora. Se llama cuando la causa del aviso murio (factura anulada OK,
    /// reserva saldada, marca de revision bajada). Idempotente: si no hay ninguno vivo con esa clave, no hace nada.
    /// Devuelve cuantos avisos apago (util para logs/tests). Clave nula o vacia = no-op (devuelve 0).
    /// </summary>
    Task<int> ResolveByKeyAsync(string? resolutionKey, CancellationToken ct = default);
}
