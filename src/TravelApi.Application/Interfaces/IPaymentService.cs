using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IPaymentService
{
    Task<CollectionsSummaryDto> GetCollectionsSummaryAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CollectionWorkItemDto>> GetCollectionsWorklistAsync(CancellationToken cancellationToken);
    Task<IEnumerable<PaymentDto>> GetAllPaymentsAsync(CancellationToken cancellationToken);
    Task<IEnumerable<PaymentDto>> GetPaymentsForReservaAsync(int ReservaId, CancellationToken cancellationToken);
    Task<PaymentDto> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);
    Task<PaymentReceiptDto> IssueReceiptAsync(int paymentId, CancellationToken cancellationToken);
    Task<byte[]> GetReceiptPdfAsync(int paymentId, CancellationToken cancellationToken);
    Task<IEnumerable<object>> GetDeletedPaymentsAsync(CancellationToken cancellationToken);
    Task<int> RestorePaymentAsync(int id, CancellationToken cancellationToken);
}

public class CreatePaymentRequest
{
    public int ReservaId { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
}
