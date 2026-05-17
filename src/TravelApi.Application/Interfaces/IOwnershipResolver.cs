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

    // FC1.2.0 v3 (2026-05-17): entidades nuevas del modulo de cancelacion/refund.
    // OperatorRefundReceived NO se incluye a proposito: es back-office sin
    // ownership por reserva (un mismo ingreso cubre N BCs). Su autorizacion
    // va por permission (CajaEdit/CobranzasEdit) en el controller.

    /// <summary>
    /// Cancelacion de reserva. Hereda el responsable de la <see cref="BookingCancellation.Reserva"/>.
    /// </summary>
    BookingCancellation,

    /// <summary>
    /// Saldo a favor del cliente. Hereda el responsable de la
    /// <see cref="ClientCreditEntry.BookingCancellation"/>.<see cref="BookingCancellation.Reserva"/>
    /// (Customer no tiene ResponsibleUserId hoy — confirmado por grep 2026-05-17).
    /// </summary>
    ClientCreditEntry,
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
