using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IInvoiceService
{
    Task<IEnumerable<InvoiceDto>> GetAllAsync(CancellationToken ct);
    Task<InvoicingSummaryDto> GetInvoicingSummaryAsync(CancellationToken ct);
    Task<IReadOnlyList<InvoicingWorkItemDto>> GetInvoicingWorklistAsync(CancellationToken ct);
    Task<InvoiceDto> CreateAsync(CreateInvoiceRequest request, string? userId, string? userName, CancellationToken ct);
    Task<bool> RetryAsync(int id, CancellationToken ct);
    Task<IEnumerable<InvoiceDto>> GetByReservaIdAsync(int reservaId, CancellationToken ct);
    Task<byte[]> GetPdfAsync(int id, CancellationToken ct);
    Task EnqueueAnnulmentAsync(int id, string userId, CancellationToken ct);
    
    // Background Job method
    Task ProcessAnnulmentJob(int invoiceId, string userId);
}
