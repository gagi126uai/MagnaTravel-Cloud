using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IInvoiceService
{
    Task<PagedResponse<InvoiceListDto>> GetAllAsync(InvoicesListQuery query, CancellationToken ct);
    Task<InvoicingSummaryDto> GetInvoicingSummaryAsync(CancellationToken ct);
    Task<PagedResponse<InvoicingWorkItemDto>> GetInvoicingWorklistAsync(InvoicingWorklistQuery query, CancellationToken ct);
    Task<InvoiceDto> CreateAsync(CreateInvoiceRequest request, string? userId, string? userName, CancellationToken ct);
    Task<bool> RetryAsync(int id, CancellationToken ct);
    Task<IEnumerable<InvoiceListDto>> GetByReservaIdAsync(int reservaId, CancellationToken ct);
    Task<byte[]> GetPdfAsync(int id, CancellationToken ct);
    // B1.15 Fase 2a (FIX 6): se agrega userName + reason para auditoria fiscal.
    // B1.15 Fase D (2026-05-11): requesterIsAdmin permite bypass del approval
    // workflow (Admin no necesita autorización para anular). approvalRequestId
    // se guarda en el job para marcar Consumed cuando la NC sea aprobada por AFIP.
    // Throws <see cref="ApprovalRequiredException"/> si el setting esta on, el
    // user no es Admin y no hay ApprovalRequest aprobado vigente.
    //
    // FC1.2.1 v3 (BR-V2-03, 2026-05-17): se agrega <c>approvalRequestId</c> opcional
    // para cross-reference fiscal cuando la annulacion la dispara
    // <c>BookingCancellationService.ConfirmAsync</c> (con un InvariantOverride
    // aprobado al BC, no un InvoiceAnnulment standalone). Si tiene valor, se
    // persiste en <c>Invoice.AnnulmentApprovalRequestId</c> para que la auditoria
    // pueda trazar quien aprobo. Callers viejos pasan null por default → compat.
    Task EnqueueAnnulmentAsync(
        int id,
        string userId,
        string? userName,
        string? reason,
        bool requesterIsAdmin,
        CancellationToken ct,
        int? approvalRequestId = null);

    // Background Job method
    // approvalRequestId nullable: si null el job no consume approval (caso Admin
    // o setting off). Si tiene valor, se marca Consumed al confirmar NC en AFIP.
    Task ProcessAnnulmentJob(int invoiceId, string userId, int? approvalRequestId = null);
}
