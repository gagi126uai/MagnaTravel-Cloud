using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IReservaService
{
    Task<ReservaListPageDto> GetReservasAsync(ReservaListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// B1.15 Fase 2a: variante de <see cref="GetReservasAsync"/> que aplica el
    /// filtro automatico "mias" segun el permiso del usuario actual:
    ///  - Si tiene <c>reservas.view_all</c> => devuelve todas (Scope = "all").
    ///  - Si NO lo tiene => devuelve solo las suyas
    ///    (<c>WHERE ResponsibleUserId = currentUserId</c>; Scope = "mine").
    /// El controller setea el header <c>X-Permission-Scope</c> con el scope efectivo.
    /// </summary>
    Task<(ReservaListPageDto Page, string Scope)> GetReservasWithScopeAsync(ReservaListQuery query, CancellationToken cancellationToken);

    Task<ReservaDto> GetReservaByIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken);
    Task<ReservaDto> CreateReservaAsync(CreateReservaRequest request, string? createdByUserId, CancellationToken cancellationToken);

    Task<ReservationServiceMutationResult> AddServiceAsync(string reservaPublicIdOrLegacyId, AddServiceRequest request, CancellationToken ct = default);
    Task<ServicioReservaDto> UpdateServiceAsync(string servicePublicIdOrLegacyId, AddServiceRequest request, CancellationToken ct = default);
    Task RemoveServiceAsync(string servicePublicIdOrLegacyId, CancellationToken ct = default);

    Task<IEnumerable<PassengerDto>> GetPassengersAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default);
    Task<PassengerDto> AddPassengerAsync(string reservaPublicIdOrLegacyId, PassengerUpsertRequest passenger, CancellationToken ct = default);
    Task<PassengerDto> UpdatePassengerAsync(string passengerPublicIdOrLegacyId, PassengerUpsertRequest updated, CancellationToken ct = default);
    Task RemovePassengerAsync(string passengerPublicIdOrLegacyId, CancellationToken ct = default);
    Task<ReservaDto> UpdatePassengerCountsAsync(string reservaPublicIdOrLegacyId, PassengerCountsRequest counts, CancellationToken ct = default);
    Task<ReservaDto> UpdateDatesAsync(string reservaPublicIdOrLegacyId, UpdateReservaDatesRequest request, CancellationToken ct = default);

    // Pasajero <-> Servicio (Phase 2.1)
    Task<IReadOnlyList<PassengerServiceAssignmentDto>> GetAssignmentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default);
    Task<PassengerServiceAssignmentDto> CreateAssignmentAsync(string reservaPublicIdOrLegacyId, CreatePassengerAssignmentRequest request, CancellationToken ct = default);
    Task RemoveAssignmentAsync(string assignmentPublicIdOrLegacyId, CancellationToken ct = default);

    Task<IEnumerable<PaymentDto>> GetReservaPaymentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default);
    Task<PaymentDto> AddPaymentAsync(string reservaPublicIdOrLegacyId, ReservationPaymentUpsertRequest payment, CancellationToken ct = default);
    Task<PaymentDto> UpdatePaymentAsync(string reservaPublicIdOrLegacyId, string paymentPublicIdOrLegacyId, ReservationPaymentUpsertRequest updatedPayment, CancellationToken ct = default);
    Task DeletePaymentAsync(string reservaPublicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken ct = default);

    /// <summary>
    /// Cambia el estado de la reserva. B1.15 Fase 2a (Decision 6): si el target
    /// es <c>Cancelled</c>, valida que <paramref name="actorUserId"/> tenga
    /// <c>reservas.cancel</c>; si la reserva tiene cobros o facturas, exige
    /// ademas <c>reservas.cancel_with_payment</c>. Falta de permiso lanza
    /// <see cref="UnauthorizedAccessException"/> (-> 403).
    /// </summary>
    Task<ReservaDto> UpdateStatusAsync(string publicIdOrLegacyId, string status, string? actorUserId, CancellationToken ct = default);
    Task<TransitionReadinessDto> GetTransitionReadinessAsync(string publicIdOrLegacyId, string targetStatus, CancellationToken ct = default);
    Task<RevertOptionsDto> GetRevertOptionsAsync(string publicIdOrLegacyId, string actorUserId, bool actorIsAdmin, CancellationToken ct = default);
    Task<ReservaDto> RevertStatusAsync(string publicIdOrLegacyId, RevertStatusRequest request, string actorUserId, string? actorUserName, bool actorIsAdmin, CancellationToken ct = default);
    Task UpdateBalanceAsync(int reservaId);
    Task<ReservaDto> ArchiveReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default);
    Task DeleteReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default);
}
