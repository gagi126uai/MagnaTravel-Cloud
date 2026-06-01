using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.2.1 v3 §2.1 (2026-05-17): orquestador del flujo de cancelacion de
/// reservas (T-1 / T0 / T2 / T3 — ver ADR-002 §2.4).
///
/// <para>
/// <b>Responsabilidad</b>: gestionar el ciclo de vida del <c>BookingCancellation</c>
/// aggregate root, coordinando con <c>InvoiceService</c> (NC fiscal),
/// <c>ApprovalRequestService</c> (overrides) y los services de refund/credit
/// (cuando lleguen en FC1.2.2/.2.3). El service NO ejecuta operaciones AFIP
/// directamente: las delega via <see cref="IInvoiceService.EnqueueAnnulmentAsync"/>.
/// </para>
///
/// <para>
/// <b>Maquina de estados</b> (resumen, ver <c>BookingCancellationStatus</c>):
/// <list type="bullet">
/// <item><c>Drafted</c> ← <see cref="DraftAsync"/></item>
/// <item><c>Drafted</c> → <c>AwaitingFiscalConfirmation</c> via <see cref="ConfirmAsync"/></item>
/// <item><c>Drafted</c> → <c>Aborted</c> via <see cref="AbortAsync"/></item>
/// <item><c>AwaitingFiscalConfirmation</c> → <c>AwaitingOperatorRefund</c> via callback
///       <see cref="IInvoiceAnnulmentBcBridge.OnArcaSucceededAsync"/> (Hangfire)</item>
/// <item><c>AwaitingFiscalConfirmation</c> → <c>AwaitingOperatorRefund</c> manual via
///       <see cref="ForceArcaConfirmationAsync"/> (admin escape hatch BR-V2-01)</item>
/// <item><c>AwaitingFiscalConfirmation</c> → <c>ArcaRejected</c> via callback
///       <see cref="IInvoiceAnnulmentBcBridge.OnArcaFailedAsync"/></item>
/// </list>
/// </para>
///
/// <para>
/// <b>FC1.2.1 alcance</b>: implementamos Draft/Confirm/Abort/ForceArca + bridge.
/// Los hooks <c>OnAllocationRecorded</c>/<c>OnAllCreditConsumed</c> se exponen en
/// la interface pero la implementacion completa de las transiciones T2/T3 llega
/// en FC1.2.2 y FC1.2.3. Hoy quedan stubs documentados.
/// </para>
/// </summary>
public interface IBookingCancellationService
{
    // ===== Comandos (UI) =====

    /// <summary>
    /// T-1: crea el BC en <c>Drafted</c>. Valida INV-081 (una sola cancelacion
    /// activa por reserva) y INV-100 (<c>OnePerReservaInvoicePolicy</c>).
    /// FiscalSnapshot queda vacio: <see cref="ConfirmAsync"/> lo completa al
    /// disparar T0.
    /// </summary>
    Task<BookingCancellationDto> DraftAsync(
        DraftCancellationRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// T0: completa el FiscalSnapshot, encola la NC en AFIP y transiciona el BC
    /// a <c>AwaitingFiscalConfirmation</c>. Setea la Reserva en
    /// <c>PendingOperatorRefund</c>. Si el caller es Admin y declara
    /// <c>IsAdminOverride=true</c>, requiere un <c>InvariantOverride</c> aprobado.
    /// Throws <c>ApprovalRequiredException</c> sino.
    /// </summary>
    Task<BookingCancellationDto> ConfirmAsync(
        Guid publicId,
        ConfirmCancellationRequest request,
        string userId,
        string? userName,
        bool requesterIsAdmin,
        CancellationToken ct);

    /// <summary>
    /// Aborta un BC en <c>Drafted</c>. Idempotente: si ya esta <c>Aborted</c>,
    /// retorna el DTO actual sin tocar nada. Si el BC esta en cualquier otro
    /// estado, throws (transicion invalida — usar el flujo normal o
    /// <see cref="ForceArcaConfirmationAsync"/> segun corresponda).
    /// </summary>
    Task<BookingCancellationDto> AbortAsync(
        Guid publicId,
        string reason,
        string userId,
        CancellationToken ct);

    /// <summary>
    /// FC1.2.1 v3 (BR-V2-01): escape hatch admin. Cuando AFIP devolvio CAE para
    /// la NC pero el callback automatico (<c>OnArcaSucceededAsync</c>) fallo
    /// (job zombie, exception no recuperable, etc.), un Admin puede empatar
    /// manualmente el estado del BC con la realidad fiscal.
    ///
    /// <para>
    /// Requiere <c>InvariantOverride</c> aprobado scoped al BC.
    /// Idempotente: si el BC ya esta en <c>AwaitingOperatorRefund</c> o adelante,
    /// retorna no-op + log warning + DTO actual (HTTP 200, no error).
    /// </para>
    /// </summary>
    Task<BookingCancellationDto> ForceArcaConfirmationAsync(
        Guid publicId,
        ForceArcaConfirmationRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    // ===== Reacciones internas (llamadas desde otros services del modulo) =====

    /// <summary>
    /// FC1.2.2 trigger: <c>OperatorRefundService.AllocateAsync</c> avisa que
    /// hubo un registro de allocation contra este BC. Si era el primero,
    /// transiciona <c>AwaitingOperatorRefund</c> → <c>ClientCreditApplied</c>.
    ///
    /// <para>
    /// <b>FC1.2.1</b>: stub no-op (implementacion real en FC1.2.2).
    /// </para>
    /// </summary>
    Task OnAllocationRecordedAsync(int bookingCancellationId, decimal netAmount, CancellationToken ct);

    /// <summary>
    /// FC1.2.2 (2026-05-18) trigger: <c>OperatorRefundService.VoidAllocationAsync</c>
    /// avisa que una allocation existente fue anulada (soft-void). Si el BC se
    /// quedo sin allocations activas, hay que volver a
    /// <c>AwaitingOperatorRefund</c> (estaba en <c>ClientCreditApplied</c>).
    ///
    /// <para>
    /// <b>FC1.2.2</b>: implementado en este service. El caller pasa
    /// <c>bookingCancellationId</c> y el <c>netAmount</c> que se libera (mismo
    /// valor que recibio antes en <see cref="OnAllocationRecordedAsync"/>).
    /// </para>
    /// </summary>
    Task OnAllocationVoidedAsync(int bookingCancellationId, decimal netAmount, CancellationToken ct);

    /// <summary>
    /// FC1.2.3 trigger: <c>ClientCreditService</c> avisa que todos los entries del
    /// BC tienen RemainingBalance=0. Cierra el BC (<c>Closed</c>) + cierra la
    /// Reserva (<c>Cancelled</c>).
    ///
    /// <para>
    /// <b>FC1.2.1</b>: stub no-op.
    /// </para>
    /// </summary>
    Task OnAllCreditConsumedAsync(int bookingCancellationId, CancellationToken ct);

    // ===== FC1.3.3 — comando publico para NC parcial (admin edita liquidacion en manual review) =====

    /// <summary>
    /// FC1.3.3 (ADR-009 §2.7 G3, 2026-05-21): el admin edita los inputs de la
    /// liquidacion fiscal de un BC que esta en
    /// <c>BookingCancellationStatus.ManualReviewPending</c>. Reglas resumidas:
    /// <list type="bullet">
    ///   <item>BC debe estar en <c>ManualReviewPending</c> (sino rechaza).</item>
    ///   <item>4-eyes (INV-FC1.3-004): admin != vendedor original, salvo bypass
    ///         GR-005 (single admin) con comentario reforzado 100+ chars.</item>
    ///   <item>El calculator vuelve a correr con los overrides y se persiste
    ///         el resultado en el <c>ApprovalRequest.Metadata.edits[]</c>.</item>
    ///   <item>El BC se queda en <c>ManualReviewPending</c> (self-loop). El
    ///         approve/reject se hace por el endpoint generico de approvals,
    ///         no por este metodo.</item>
    ///   <item>Audit obligatorio (<c>BookingCancellationLiquidationEdited</c>)
    ///         con diff JSON {"Field":{"Old":"...","New":"..."}}.</item>
    /// </list>
    ///
    /// <para><b>Approve/Reject NO se exponen como metodos publicos del service</b>:
    /// el flujo canonico es invocar <c>ApprovalRequestService.ApproveAsync</c> /
    /// <c>RejectAsync</c> del controller generico de approvals, que despues
    /// dispara los callbacks del bridge <c>IPartialCreditNoteApprovalBridge</c>.
    /// Asi se evita duplicar la maquina de estados del approval en dos lados.</para>
    /// </summary>
    Task<BookingCancellationDto> EditLiquidationAsync(
        Guid publicId,
        EditLiquidationRequest req,
        string userId,
        string? userName,
        CancellationToken ct);

    // ===== Queries =====

    /// <summary>Obtiene un BC por su PublicId. Null si no existe.</summary>
    Task<BookingCancellationDto?> GetByPublicIdAsync(Guid publicId, CancellationToken ct);

    /// <summary>
    /// ADR-013 §3.10 (M4, 2026-06-01): bandeja "cancelaciones con NC emitida pero sin su
    /// ND". Devuelve los BCs cuya NC total ya salio (CreditNoteInvoiceId seteado) pero cuya
    /// ND quedo en <c>Pending</c> o <c>Failed</c> -> fiscalmente incompletas.
    ///
    /// <para>Como efecto secundario RECONCILIA el estado de la ND: para los que estan en
    /// <c>Pending</c>, lee el <c>Resultado</c> de la Invoice ND vinculada (que la emite el
    /// job async) y, si ya tiene CAE (Aprobado) o fue Rechazada, transiciona el
    /// <c>DebitNoteStatus</c> a <c>Issued</c>/<c>Failed</c>. Asi la bandeja se va limpiando
    /// sola a medida que ARCA responde, sin necesitar un callback dedicado.</para>
    /// </summary>
    Task<IReadOnlyList<CancellationDebitNotePendingDto>> GetCancellationsWithMissingDebitNoteAsync(
        CancellationToken ct);
}
