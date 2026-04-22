using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IPaymentService
{
    Task<CollectionsSummaryDto> GetCollectionsSummaryAsync(CancellationToken cancellationToken);
    Task<PagedResponse<CollectionWorkItemDto>> GetCollectionsWorklistAsync(CollectionWorklistQuery query, CancellationToken cancellationToken);
    Task<PagedResponse<PaymentDto>> GetAllPaymentsAsync(PaymentsListQuery query, CancellationToken cancellationToken);
    Task<PagedResponse<FinanceHistoryItemDto>> GetHistoryAsync(FinanceHistoryQuery query, CancellationToken cancellationToken);
    Task<IEnumerable<PaymentDto>> GetPaymentsForReservaAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<PaymentDto> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);
    Task<PaymentReceiptDto> IssueReceiptAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<byte[]> GetReceiptPdfAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<IEnumerable<object>> GetDeletedPaymentsAsync(CancellationToken cancellationToken);
    Task<Guid> RestorePaymentAsync(string paymentPublicIdOrLegacyId, CancellationToken cancellationToken);
}

public class CreatePaymentRequest
{
    public string ReservaId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
}
