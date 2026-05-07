namespace TravelApi.Application.Interfaces;

/// <summary>
/// B1.15 Fase 1: enum de entidades que el sistema sabe validar por ownership.
/// Conocido por <see cref="IOwnershipResolver"/> y por el filter
/// <c>RequireOwnershipAttribute</c>.
/// </summary>
public enum OwnedEntity
{
    Reserva,
    Servicio,
    Payment,
    Invoice,
    Voucher,
    Passenger,
    Assignment,
}

/// <summary>
/// B1.15 Fase 1: dado un userId y una entidad referenciada por su id publico
/// (Guid) o legacy (int), devuelve si el usuario es el responsable.
///
/// Si la entidad no tiene <c>ResponsibleUserId</c> seteado (legacy), DEBE
/// rechazar (return false) — la decision es bloqueante para que el comando
/// <c>users.set-responsible</c> haga el backfill antes de migrar controllers.
/// </summary>
public interface IOwnershipResolver
{
    /// <summary>
    /// Devuelve true si el usuario es el responsable de la entidad referenciada.
    /// Devuelve false si la entidad no existe, no tiene responsable o no coincide.
    /// </summary>
    Task<bool> IsOwnerAsync(
        string userId,
        OwnedEntity entity,
        string publicIdOrLegacyId,
        CancellationToken cancellationToken = default);
}
