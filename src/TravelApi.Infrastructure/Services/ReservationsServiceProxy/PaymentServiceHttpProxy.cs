using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services.ReservationsServiceProxy;

public class PaymentServiceHttpProxy : ReservationsServiceHttpProxyBase, IPaymentService
{
    public PaymentServiceHttpProxy(HttpClient httpClient)
        : base(httpClient)
    {
    }

    public Task<CollectionsSummaryDto> GetCollectionsSummaryAsync(CancellationToken cancellationToken)
        => GetAsync<CollectionsSummaryDto>("api/payments/collections-summary", cancellationToken);

    public Task<PagedResponse<CollectionWorkItemDto>> GetCollectionsWorklistAsync(CollectionWorklistQuery query, CancellationToken cancellationToken)
        => GetAsync<PagedResponse<CollectionWorkItemDto>>(WithQuery("api/payments/collections-worklist", query), cancellationToken);

    public Task<PagedResponse<PaymentDto>> GetAllPaymentsAsync(PaymentsListQuery query, CancellationToken cancellationToken)
        => GetAsync<PagedResponse<PaymentDto>>(WithQuery("api/payments", query), cancellationToken);

    public Task<PagedResponse<FinanceHistoryItemDto>> GetHistoryAsync(FinanceHistoryQuery query, CancellationToken cancellationToken)
        => GetAsync<PagedResponse<FinanceHistoryItemDto>>(WithQuery("api/payments/history", query), cancellationToken);

    public Task<IEnumerable<PaymentDto>> GetPaymentsForReservaAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
        => GetAsync<IEnumerable<PaymentDto>>($"api/payments/reserva/{reservaPublicIdOrLegacyId}", cancellationToken);

    public Task<PaymentDto> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
        => PostAsync<CreatePaymentRequest, PaymentDto>("api/payments", request, cancellationToken);

    public Task<PaymentReceiptDto> IssueReceiptAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
        => PostAsync<object, PaymentReceiptDto>($"api/payments/{paymentPublicIdOrLegacyId}/receipt", new { }, cancellationToken);

    public Task<byte[]> GetReceiptPdfAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
        => GetBytesAsync($"api/payments/{paymentPublicIdOrLegacyId}/receipt/pdf", cancellationToken);

    public Task<IEnumerable<object>> GetDeletedPaymentsAsync(CancellationToken cancellationToken)
        => GetAsync<IEnumerable<object>>("api/payments/trash", cancellationToken);

    public Task<Guid> RestorePaymentAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
        => PutAsync<object, RestorePaymentResponse>($"api/payments/{paymentPublicIdOrLegacyId}/restore", new { }, cancellationToken)
            .ContinueWith(task => task.Result.PaymentPublicId, cancellationToken);

    private sealed class RestorePaymentResponse
    {
        public Guid PaymentPublicId { get; set; }
    }
}
