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

    // Hallazgo auditoria ERP #9 (2026-06-13): arma los items de factura SUGERIDOS desde los servicios
    // CONFIRMADOS de la reserva (una linea por servicio, por moneda) para que el modal de creacion
    // prerrellene desde los servicios reales en vez del unico item generico "Servicios Turisticos".
    // Solo LECTURA: no crea ni muta nada. Throws InvalidOperationException si la reserva no existe.
    Task<InvoiceSuggestedItemsResponse> GetSuggestedItemsAsync(int reservaId, CancellationToken ct);

    Task<bool> RetryAsync(int id, CancellationToken ct);
    Task<IEnumerable<InvoiceListDto>> GetByReservaIdAsync(int reservaId, CancellationToken ct);

    // H2 (2026-06-24): estado fiscal CLARO de las facturas de una reserva, para el POLL del front.
    // La emision es ASINCRONA (POST /invoices encola; un job pide el CAE en segundo plano). Este metodo
    // es SOLO LECTURA: traduce el Resultado crudo de cada factura ("PENDING"/"A"/"R") a InProcess/Issued/
    // Rejected, expone numero+CAE+vencimiento cuando esta emitida y el motivo de rechazo (Observaciones)
    // cuando ARCA la rechazo. NO dispara emision. Devuelve las facturas mas recientes primero.
    Task<IEnumerable<InvoiceFiscalStatusDto>> GetFiscalStatusByReservaIdAsync(int reservaId, CancellationToken ct);

    // Fase 4 (2026-06-26): PRE-CHEQUEO de emision. Deriva la letra que se emitiria hoy (misma matriz fiscal que
    // CreatePendingInvoice) y avisa el unico bloqueo duro: cliente que recibiria Factura A pero sin CUIT (ARCA
    // rebota). SOLO LECTURA: no encola ni muta nada. Throws InvalidOperationException si la reserva no existe.
    Task<InvoiceEmissionPreflightDto> GetEmissionPreflightAsync(int reservaId, CancellationToken ct);

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

    /// <summary>
    /// ADR-042 F1 (2026-07-02): agenda la anulacion de una factura que el caller YA marco
    /// <c>AnnulmentStatus=Pending</c> BAJO SU PROPIO LOCK (retry de NC multi-factura serializado por el
    /// <c>FOR UPDATE</c> del BC). A diferencia de <see cref="EnqueueAnnulmentAsync"/>, NO re-aplica el guard
    /// "Pending -&gt; throw" (la marca es propia del retry, no un doble-click) ni re-escribe AnnulmentStatus:
    /// solo agenda el job de Hangfire (I/O fuera del lock). Sigue rechazando Succeeded (no re-anular una
    /// factura ya anulada). Es <c>requesterIsAdmin</c> por definicion (la anulacion se autorizo al confirmar).
    /// </summary>
    Task EnqueueAnnulmentRetryAsync(
        int id,
        string userId,
        string? userName,
        string? reason,
        int? approvalRequestId,
        CancellationToken ct);

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

    // FC1.3.F2.6a (rehecho 2026-05-28): reconcilia UNA NC parcial colgada en
    // Resultado='PENDING' reutilizando EXACTAMENTE el mecanismo de stale-key
    // recovery del emisor (HandleStaleIdempotencyKeyAsync, F2.2). Lo invoca el
    // job recurrente PartialCreditNotePostingReconciliationJob.
    //
    // POR QUE vive aca y no en el job: la logica fiscal (consultar ARCA con el
    // numerador REAL capturado antes del POST, matchear por comprobante asociado
    // y NO por monto, derivar el CAE, anular la factura origen) ya existe en
    // InvoiceService y toca ARCA + ArcaIdempotencyKeys. Centralizarla evita que
    // el job reimplemente — y desincronice — esa logica (ese fue el origen de los
    // bugs B-1/B-2 de la revision). El job queda fino: agenda + escalado.
    //
    // Que hace, dado el Id de una NC parcial PENDING:
    //  1. Ubica su ArcaIdempotencyKey (re-derivando la idemKey deterministica a
    //     partir de datos persistidos de la NC + factura origen).
    //  2. Si la key existe y NO esta vencida -> el emisor todavia esta en vuelo:
    //     devuelve InFlight, NO toca nada (arregla M-1).
    //  3. Si la key existe y esta vencida (huerfana de crash) -> corre el MISMO
    //     recovery del emisor: consulta ARCA con el LastSeenNumeroBeforePost REAL
    //     (arregla B-1) y matchea por comprobante asociado (arregla B-2). Si ARCA
    //     confirma -> confirma la NC + anula la origen y devuelve Confirmed. Si no
    //     -> borra la key huerfana, re-encola la emision idempotente y devuelve
    //     ReEnqueuedEmission.
    //  4. Si la NC NO tiene key (nunca llego a reservar numero / postear) ->
    //     re-encola la emision idempotente y devuelve ReEnqueuedEmission. NUNCA
    //     confirma a ciegas.
    //
    // NO notifica admins ni aplica rate-limit: eso es responsabilidad del job
    // (anti-spam + escalado a manual viven en la capa de agenda).
    Task<PartialCreditNotePostingReconcileResult> ReconcileStuckPartialCreditNoteAsync(
        int creditNoteInvoiceId,
        CancellationToken ct);
}
