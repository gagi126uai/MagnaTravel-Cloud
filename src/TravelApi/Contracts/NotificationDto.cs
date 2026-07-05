using TravelApi.Domain.Entities;

namespace TravelApi.Contracts;

/// <summary>
/// (Tanda 5, 2026-07-05 — data-exposure gate) Proyección de <see cref="Notification"/> hacia el navegador, usada por
/// AMBAS vías que llegan al front: la API REST (<c>GET /api/notifications</c> y <c>/urgent</c>) y el push en tiempo
/// real por SignalR (<c>ReceiveNotification</c> / <c>ReceiveUrgentBanner</c>).
///
/// <para>La entidad EF lleva campos SOLO internos que un agente de viajes no debe ver y que el front NO consume:
/// <c>UserId</c> (identidad), <c>IsRead</c>/<c>IsDismissed</c>, <c>RelatedEntityType</c>/<c>RelatedEntityId</c>
/// (nombre de clase interna + id de base cruda) y —desde esta tanda— <c>ResolutionKey</c> (claves técnicas como
/// <c>"Invoice:42"</c>, <c>"ReservaNeedsReview:17"</c>, <c>"CoherenceWatchdog:daily"</c>) y <c>ResolvedAt</c>.
/// Devolver la entidad cruda filtraría esos códigos/claves internas por el cuerpo de la API y por la trama SignalR.
/// Este DTO expone SOLO lo que la campanita y el banner urgente realmente renderizan.</para>
///
/// <para>Campos en camelCase (serialización web) idénticos al contrato previo del front:
/// <c>id, message, type, priority, createdAt</c>. No agregar campos internos acá.</para>
/// </summary>
public sealed record NotificationDto(
    int Id,
    string Message,
    string Type,
    string Priority,
    DateTime CreatedAt)
{
    public static NotificationDto FromEntity(Notification n) =>
        new(n.Id, n.Message, n.Type, n.Priority, n.CreatedAt);
}
