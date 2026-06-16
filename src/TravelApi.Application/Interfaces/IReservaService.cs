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

    /// <summary>
    /// ADR-031 v2.1: cobertura nominal POR SERVICIO (set de pasajeros + nombres faltantes), para que el
    /// mini-form del front sepa a quien pedirle los datos. Misma resolucion del set y misma regla por tipo
    /// que el gate del backend. Respuesta sin numero de documento.
    /// </summary>
    Task<ServiceNominalCoverageDto> GetServiceNominalCoverageAsync(
        string reservaPublicIdOrLegacyId, string serviceType, string servicePublicIdOrLegacyId, CancellationToken ct = default);

    /// <summary>
    /// ADR-031 v2.1: REEMPLAZO TOTAL ATOMICO del set de pasajeros de un servicio. Borra las asignaciones
    /// actuales del servicio y deja EXACTAMENTE las del request, todo en una sola unidad de trabajo (un
    /// solo SaveChanges) + auditoria en la misma transaccion. Normaliza "todos" a CERO asignaciones
    /// (invariante "todos = sin asignaciones"). Idempotente. Devuelve el <see cref="ServiceNominalCoverageDto"/>
    /// resultante (mismo shape que el GET) para que el front no re-pida. Sin numero de documento.
    /// </summary>
    Task<ServiceNominalCoverageDto> ReplaceServiceAssignmentsAsync(
        string reservaPublicIdOrLegacyId, string serviceType, string servicePublicIdOrLegacyId,
        ReplaceServiceAssignmentsRequest request, CancellationToken ct = default);

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

    /// <summary>
    /// ADR-020 F4 (candado): crea una autorizacion VIVA para editar una reserva confirmada
    /// (Confirmed en adelante). Si el actor tiene <c>reservas.authorize_locked_edit</c> se
    /// auto-autoriza; si no, debe indicar un autorizante que lo tenga. La nueva autorizacion
    /// expira cualquier otra viva de la misma reserva (una sola viva a la vez). Lanza
    /// <see cref="InvalidOperationException"/> (-> 409) si la reserva no esta bajo candado o
    /// si nadie con permiso autoriza.
    /// </summary>
    Task<ReservaEditAuthorizationDto> CreateEditAuthorizationAsync(string publicIdOrLegacyId, CreateEditAuthorizationRequest request, string actorUserId, string? actorUserName, bool actorIsAdmin, CancellationToken ct = default);

    Task UpdateBalanceAsync(int reservaId);

    /// <summary>
    /// ADR-027 (hallazgo #10): igual que <see cref="UpdateBalanceAsync(int)"/> pero los paths de EDICION de
    /// servicio pasan <paramref name="markChangesIfMeaningfulOnLive"/>=true cuando detectaron un cambio de
    /// precio/costo; si la reserva esta en estado vivo, queda marcada "confirmada con cambios" para revision.
    /// </summary>
    Task UpdateBalanceAsync(int reservaId, bool markChangesIfMeaningfulOnLive);

    /// <summary>
    /// ADR-027 (detalle, 2026-06-13): igual que la sobrecarga con bool, pero ademas REGISTRA el detalle del
    /// cambio (que servicio, que campo, de cuanto a cuanto, moneda, quien/cuando) si <paramref name="change"/>
    /// trae un cambio significativo y la reserva esta en estado vivo. Pasar <c>null</c> = no marcar ni
    /// registrar (equivalente a la sobrecarga sin flag). El detalle se limpia al dar el OK (acknowledge).
    /// </summary>
    Task UpdateBalanceAsync(int reservaId, Contracts.Reservations.PendingServiceChange? change);

    /// <summary>
    /// ADR-027 (hallazgo #10): el dueño da el OK a los cambios de una reserva "confirmada con cambios".
    /// Limpia <c>HasUnacknowledgedChanges</c> y registra quien/cuando (auditoria). No-op idempotente si la
    /// reserva no estaba marcada. Lanza <see cref="KeyNotFoundException"/> si la reserva no existe.
    /// </summary>
    Task<ReservaDto> AcknowledgeChangesAsync(string publicIdOrLegacyId, string actorUserId, string? actorUserName, CancellationToken ct = default);

    Task<ReservaDto> ArchiveReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default);
    Task DeleteReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default);
}
