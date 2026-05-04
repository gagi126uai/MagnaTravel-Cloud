using System.Text.Json;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services.ReservationsServiceProxy;

public class ReservaServiceHttpProxy : ReservationsServiceHttpProxyBase, IReservaService
{
    public ReservaServiceHttpProxy(HttpClient httpClient)
        : base(httpClient)
    {
    }

    public Task<ReservaListPageDto> GetReservasAsync(ReservaListQuery query, CancellationToken cancellationToken)
        => GetAsync<ReservaListPageDto>(WithQuery("api/reservas", query), cancellationToken);

    public Task<ReservaDto> GetReservaByIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
        => GetAsync<ReservaDto>($"api/reservas/{publicIdOrLegacyId}", cancellationToken);

    public Task<ReservaDto> CreateReservaAsync(CreateReservaRequest request, string? createdByUserId, CancellationToken cancellationToken)
        => PostAsync<CreateReservaRequest, ReservaDto>("api/reservas", request, cancellationToken);

    public async Task<ReservationServiceMutationResult> AddServiceAsync(string reservaPublicIdOrLegacyId, AddServiceRequest request, CancellationToken ct = default)
    {
        using var document = await PostForDocumentAsync($"api/reservas/{reservaPublicIdOrLegacyId}/services", request, ct);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("servicio", out var servicioElement))
        {
            return new ReservationServiceMutationResult
            {
                Servicio = servicioElement.Deserialize<ServicioReservaDto>() ?? throw new InvalidOperationException("Reservations service returned an empty service payload."),
                Warning = root.TryGetProperty("warning", out var warningElement) ? warningElement.GetString() :
                    root.TryGetProperty("Warning", out var warningPascal) ? warningPascal.GetString() : null
            };
        }

        return new ReservationServiceMutationResult
        {
            Servicio = root.Deserialize<ServicioReservaDto>() ?? throw new InvalidOperationException("Reservations service returned an invalid service payload."),
            Warning = null
        };
    }

    public Task<ServicioReservaDto> UpdateServiceAsync(string servicePublicIdOrLegacyId, AddServiceRequest request, CancellationToken ct = default)
        => PutAsync<AddServiceRequest, ServicioReservaDto>($"api/reservas/services/{servicePublicIdOrLegacyId}", request, ct);

    public Task RemoveServiceAsync(string servicePublicIdOrLegacyId, CancellationToken ct = default)
        => DeleteAsync($"api/reservas/services/{servicePublicIdOrLegacyId}", ct);

    public Task<IEnumerable<PassengerDto>> GetPassengersAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default)
        => GetAsync<IEnumerable<PassengerDto>>($"api/reservas/{reservaPublicIdOrLegacyId}/passengers", ct);

    public Task<PassengerDto> AddPassengerAsync(string reservaPublicIdOrLegacyId, PassengerUpsertRequest passenger, CancellationToken ct = default)
        => PostAsync<PassengerUpsertRequest, PassengerDto>($"api/reservas/{reservaPublicIdOrLegacyId}/passengers", passenger, ct);

    public Task<PassengerDto> UpdatePassengerAsync(string passengerPublicIdOrLegacyId, PassengerUpsertRequest updated, CancellationToken ct = default)
        => PutAsync<PassengerUpsertRequest, PassengerDto>($"api/reservas/passengers/{passengerPublicIdOrLegacyId}", updated, ct);

    public Task RemovePassengerAsync(string passengerPublicIdOrLegacyId, CancellationToken ct = default)
        => DeleteAsync($"api/reservas/passengers/{passengerPublicIdOrLegacyId}", ct);

    public Task<ReservaDto> UpdatePassengerCountsAsync(string reservaPublicIdOrLegacyId, PassengerCountsRequest counts, CancellationToken ct = default)
        => PatchAsync<PassengerCountsRequest, ReservaDto>($"api/reservas/{reservaPublicIdOrLegacyId}/passenger-counts", counts, ct);

    public Task<TransitionReadinessDto> GetTransitionReadinessAsync(string reservaPublicIdOrLegacyId, string targetStatus, CancellationToken ct = default)
        => GetAsync<TransitionReadinessDto>($"api/reservas/{reservaPublicIdOrLegacyId}/transition-readiness?to={Uri.EscapeDataString(targetStatus)}", ct);

    public Task<RevertOptionsDto> GetRevertOptionsAsync(string publicIdOrLegacyId, string actorUserId, bool actorIsAdmin, CancellationToken ct = default)
        => GetAsync<RevertOptionsDto>($"api/reservas/{publicIdOrLegacyId}/revert-options", ct);

    public Task<ReservaDto> RevertStatusAsync(string publicIdOrLegacyId, RevertStatusRequest request, string actorUserId, string? actorUserName, bool actorIsAdmin, CancellationToken ct = default)
        => PostAsync<RevertStatusRequest, ReservaDto>($"api/reservas/{publicIdOrLegacyId}/revert-status", request, ct);

    public async Task<IReadOnlyList<PassengerServiceAssignmentDto>> GetAssignmentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var list = await GetAsync<List<PassengerServiceAssignmentDto>>($"api/reservas/{reservaPublicIdOrLegacyId}/assignments", ct);
        return list;
    }

    public Task<PassengerServiceAssignmentDto> CreateAssignmentAsync(string reservaPublicIdOrLegacyId, CreatePassengerAssignmentRequest request, CancellationToken ct = default)
        => PostAsync<CreatePassengerAssignmentRequest, PassengerServiceAssignmentDto>($"api/reservas/{reservaPublicIdOrLegacyId}/assignments", request, ct);

    public Task RemoveAssignmentAsync(string assignmentPublicIdOrLegacyId, CancellationToken ct = default)
        => DeleteAsync($"api/reservas/assignments/{assignmentPublicIdOrLegacyId}", ct);

    public Task<IEnumerable<PaymentDto>> GetReservaPaymentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default)
        => GetAsync<IEnumerable<PaymentDto>>($"api/reservas/{reservaPublicIdOrLegacyId}/payments", ct);

    public Task<PaymentDto> AddPaymentAsync(string reservaPublicIdOrLegacyId, ReservationPaymentUpsertRequest payment, CancellationToken ct = default)
        => PostAsync<ReservationPaymentUpsertRequest, PaymentDto>($"api/reservas/{reservaPublicIdOrLegacyId}/payments", payment, ct);

    public Task<PaymentDto> UpdatePaymentAsync(string reservaPublicIdOrLegacyId, string paymentPublicIdOrLegacyId, ReservationPaymentUpsertRequest updatedPayment, CancellationToken ct = default)
        => PutAsync<ReservationPaymentUpsertRequest, PaymentDto>($"api/reservas/{reservaPublicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}", updatedPayment, ct);

    public Task DeletePaymentAsync(string reservaPublicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken ct = default)
        => DeleteAsync($"api/reservas/{reservaPublicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}", ct);

    public Task<ReservaDto> UpdateStatusAsync(string publicIdOrLegacyId, string status, CancellationToken ct = default)
        => PutAsync<object, ReservaDto>($"api/reservas/{publicIdOrLegacyId}/status", new { status }, ct);

    public Task UpdateBalanceAsync(int reservaId)
        => throw new NotSupportedException("Remote reservations service does not expose UpdateBalanceAsync directly.");

    public Task<ReservaDto> ArchiveReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default)
        => PutAsync<object, ReservaDto>($"api/reservas/{publicIdOrLegacyId}/archive", new { }, ct);

    public Task DeleteReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default)
        => DeleteAsync($"api/reservas/{publicIdOrLegacyId}", ct);
}
