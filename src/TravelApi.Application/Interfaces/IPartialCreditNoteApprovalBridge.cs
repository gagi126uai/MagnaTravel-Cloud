namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.3.2 (ADR-009 §2.7, 2026-05-21): interface "chica" hermana de
/// <see cref="IInvoiceAnnulmentBcBridge"/>. Expone los 2 callbacks que
/// <c>ApprovalRequestService.ApproveAsync</c> / <c>RejectAsync</c> tienen que
/// invocar cuando el <c>ApprovalRequest</c> que se aprobo / rechazo es del tipo
/// <c>PartialCreditNoteApproval</c>.
///
/// <para>
/// <b>Por que una interface aparte y no usar <see cref="IBookingCancellationService"/></b>:
/// mismo motivo que <see cref="IInvoiceAnnulmentBcBridge"/> — si
/// <c>ApprovalRequestService</c> inyectara <c>IBookingCancellationService</c>
/// completo, abrimos un grafo DI bidireccional (BC ya inyecta
/// <c>IApprovalRequestService</c>). Con esta interface "chica" el caller
/// solo conoce los 2 metodos minimos que necesita y el ciclo queda solo
/// logico (callback en runtime), no en el grafo de tipos.
/// </para>
///
/// <para>
/// <b>Implementacion en BookingCancellationService</b>: la clase concreta
/// implementa simultaneamente <c>IBookingCancellationService</c> +
/// <c>IInvoiceAnnulmentBcBridge</c> + <c>IPartialCreditNoteApprovalBridge</c>.
/// El DI registra la clase concreta una vez y cada interface se resuelve
/// como factory que devuelve la MISMA instancia dentro del scope (patron
/// MR-V2-02 heredado de FC1.2). Esto es critico: si fueran instancias
/// distintas, los callbacks no verian los cambios commiteados por el
/// flujo principal (cada instancia tendria su propio ChangeTracker).
/// </para>
///
/// <para>
/// <b>Reglas de idempotencia</b> (ADR-009 §2.7 N-007): ambos metodos son
/// idempotentes. Si el BC ya esta en
/// <c>BookingCancellationStatus.ManualReviewApproved</c> (caso
/// <see cref="OnApprovedAsync"/>) o <c>ManualReviewRejected</c>
/// (caso <see cref="OnRejectedAsync"/>), el callback loguea y retorna sin
/// hacer nada. Si NO matchea el BC esperado (porque la AR no esta vinculada
/// a ningun BC o porque el estado es diferente), tambien log + return —
/// nunca tirar excepcion al caller para no romper el flujo de approval.
/// La divergencia eventual entre AR y BC se sanea por el job de
/// reconciliacion (FC1.3.6b) + force-callback admin (§2.12 ADR).
/// </para>
///
/// <para>
/// <b>Transaccionalidad</b> (ADR-009 §2.7 N-007 round 3): la AR se persiste
/// en una tx y, despues del commit, se invoca este bridge en una tx aparte.
/// Si la 2da tx falla, la AR queda Approved y el BC queda
/// <c>ManualReviewPending</c> — situacion esperada y saneada por el job de
/// reconciliacion. Una tx distribuida cross-service seria overkill.
/// </para>
/// </summary>
public interface IPartialCreditNoteApprovalBridge
{
    /// <summary>
    /// Callback que <c>ApprovalRequestService.ApproveAsync</c> dispara cuando
    /// la <c>ApprovalRequest</c> aprobada es del tipo
    /// <c>PartialCreditNoteApproval</c>. Transiciona el BC asociado de
    /// <c>BookingCancellationStatus.ManualReviewPending</c> a
    /// <c>ManualReviewApproved</c> y dispara la emision real de la NC parcial
    /// (encolando el job AFIP via <c>InvoiceService.EnqueuePartialCreditNoteAsync</c>).
    ///
    /// <para>Idempotencia: si el BC ya esta en <c>ManualReviewApproved</c> o
    /// posterior (Approved/AwaitingFiscalConfirmation/etc.), log warning + return
    /// sin tirar excepcion.</para>
    /// </summary>
    /// <param name="approvalRequestId">Id del <c>ApprovalRequest</c> que se aprobo. El callee lo usa
    ///   para buscar el BC matchante via <c>BC.PartialCreditNoteApprovalRequestId</c>.</param>
    /// <param name="resolverUserId">Id del Admin/Colaborador que aprobo (para audit).</param>
    /// <param name="resolverUserName">Nombre display del resolver (para audit). Puede ser null si
    ///   el resolver fue dado de baja entre approve y callback.</param>
    /// <param name="resolverNotes">Comentario del resolver (FluentValidation ya valido min length
    ///   segun threshold). Puede ser null si el threshold no lo requirio.</param>
    Task OnApprovedAsync(int approvalRequestId, string resolverUserId, string? resolverUserName, string? resolverNotes, CancellationToken ct);

    /// <summary>
    /// Callback simetrico al <see cref="OnApprovedAsync"/>. Lo dispara
    /// <c>ApprovalRequestService.RejectAsync</c>. Transiciona el BC de
    /// <c>ManualReviewPending</c> a <c>ManualReviewRejected</c> y, segun la
    /// politica de FC1.3 (ADR-009 §2.8), auto-resetea a <c>Drafted</c> para
    /// que el vendedor pueda corregir y reenviar (con cooldown anti-spam).
    ///
    /// <para>Idempotencia: si el BC ya esta en <c>ManualReviewRejected</c> o
    /// posterior (Drafted/Aborted), log warning + return.</para>
    /// </summary>
    /// <param name="approvalRequestId">Id del <c>ApprovalRequest</c> que se rechazo.</param>
    /// <param name="resolverUserId">Id del Admin/Colaborador que rechazo.</param>
    /// <param name="resolverUserName">Nombre display del resolver. Puede ser null.</param>
    /// <param name="resolverNotes">Motivo del rechazo. Obligatorio en RejectAsync (la validacion
    ///   vive en el service del approval, no aca).</param>
    Task OnRejectedAsync(int approvalRequestId, string resolverUserId, string? resolverUserName, string? resolverNotes, CancellationToken ct);
}
