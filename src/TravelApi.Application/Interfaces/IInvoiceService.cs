using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
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

    // FC1.3.F2.2 (plan tactico §FC1.3.F2.2, 2026-05-27): emite una Nota de Credito
    // (NC) PARCIAL real al ARCA. A diferencia de EnqueueAnnulmentAsync (que emite
    // NC TOTAL replicando 1:1 los items de la factura origen), este metodo recibe
    // la liquidacion ya calculada (montos + lineas + moneda) y, en el job posterior,
    // prorratea IVA y emite una NC por solo una parte de la factura.
    //
    // Este metodo NO toca ARCA ni la tabla ArcaIdempotencyKeys: solo valida la
    // coherencia de los montos del input, marca la factura como anulacion en curso
    // (AnnulmentStatus = Pending) y encola el job ProcessPartialCreditNoteJob.
    // El snapshot del numerador ARCA + la idempotencia + el POST real son
    // responsabilidad del job (RH4-001), porque entre encolar y ejecutar el job
    // pueden pasar varios minutos y el numerador ARCA puede avanzar por otros emisores.
    //
    // Idempotencia: rechaza si la factura origen ya esta en Pending o Succeeded
    // (misma regla que EnqueueAnnulmentAsync). Permite reintento desde Failed.
    //
    // Throws:
    //  - KeyNotFoundException si la factura origen no existe.
    //  - InvalidOperationException si la factura ya tiene anulacion en curso/exitosa,
    //    o si el tipo de comprobante no soporta NC parcial (Factura M, RH-003).
    //  - ArgumentException si los montos del input no son coherentes entre si
    //    (validacion defensiva PRE-encolado, M4). En ese caso NO muta la factura
    //    ni encola el job (cero side-effects).
    Task EnqueuePartialCreditNoteAsync(
        int originalInvoiceId,
        PartialCreditNoteEmissionInput liquidation,
        string userId,
        string? userName,
        string? reason,
        int approvalRequestId,
        CancellationToken ct);

    // FC1.3.F2.2 — Background Job (ESQUELETO, Etapa 4 de 5).
    //
    // ATENCION: el cuerpo real de este job (snapshot numerador + idempotencia
    // ArcaIdempotencyKeys + stale key recovery + prorrateo IVA + CreatePendingInvoice
    // + POST al ARCA) se implementa en la Etapa 5. Hoy el cuerpo solo lanza
    // NotImplementedException. El metodo existe en la interfaz porque
    // EnqueuePartialCreditNoteAsync lo encola via
    // _backgroundJobClient.Enqueue<IInvoiceService>(s => s.ProcessPartialCreditNoteJob(...))
    // y Hangfire resuelve la expresion contra el TIPO de la interfaz: sin la
    // declaracion aca, el encolado no compilaria.
    //
    // El input se pasa serializado como JSON (liquidationJson) porque Hangfire no
    // serializa de forma confiable un record con IReadOnlyList anidada sin
    // configuracion extra. El job lo deserializa al arrancar (Etapa 5).
    Task ProcessPartialCreditNoteJob(
        int originalInvoiceId,
        string liquidationJson,
        string userId,
        int approvalRequestId);
}
