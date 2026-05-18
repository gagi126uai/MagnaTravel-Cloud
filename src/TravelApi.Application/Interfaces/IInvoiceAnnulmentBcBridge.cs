namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.2.1 v3 §2.1.bis (MR-V2-02 / BR-V2-04, 2026-05-17): interface "chica" que
/// expone <b>solo</b> los 2 callbacks que el job de Hangfire
/// <c>InvoiceService.ProcessAnnulmentJob</c> invoca cuando AFIP termina de
/// procesar la NC. La implementacion concreta vive en
/// <c>BookingCancellationService</c>, que implementa tambien la interface
/// publica <see cref="IBookingCancellationService"/>.
///
/// <para>
/// <b>Por que dos interfaces para la misma clase</b>: sin este split,
/// <c>InvoiceService</c> inyectaria <c>IBookingCancellationService</c> y
/// <c>BookingCancellationService</c> inyectaria <c>IInvoiceService</c> →
/// ciclo de dependencias DI rechazado por el resolver al startup ("circular
/// scoped dependency"). Con el split, el ciclo queda solo logico (uno llama al
/// otro en tiempo de ejecucion) pero NO en el grafo de tipos del DI container.
/// </para>
///
/// <para>
/// <b>Reglas de idempotencia</b> (HC3 plan v3 §6.1.bis): cada metodo busca el
/// BC matchante por <c>OriginatingInvoiceId AND Status=AwaitingFiscalConfirmation</c>.
/// Si no encuentra (porque ya transiciono via <c>ForceArcaConfirmationAsync</c>,
/// porque el reintento Hangfire ya corrio, etc.) → log warning + return (no-op).
/// Nunca tirar excepcion al caller para que el job de Hangfire no quede en retry
/// loop (la NC fiscal ya esta commiteada, reintentar la llamada AFIP seria peor).
/// </para>
/// </summary>
public interface IInvoiceAnnulmentBcBridge
{
    /// <summary>
    /// Callback que <c>ProcessAnnulmentJob</c> dispara cuando AFIP aprueba la NC
    /// (CAE recibido). Transiciona el BC asociado de
    /// <c>AwaitingFiscalConfirmation</c> a <c>AwaitingOperatorRefund</c>,
    /// setea <c>CreditNoteInvoiceId</c>, transiciona la Reserva a
    /// <c>PendingOperatorRefund</c> y registra audit
    /// <c>BookingCancellationArcaSucceeded</c>.
    /// </summary>
    /// <param name="originatingInvoiceId">Id (legacy int) de la factura original que se anulo.</param>
    /// <param name="creditNoteInvoiceId">Id de la nueva Invoice tipo NC emitida por AFIP.</param>
    Task OnArcaSucceededAsync(int originatingInvoiceId, int creditNoteInvoiceId, CancellationToken ct);

    /// <summary>
    /// Callback que <c>ProcessAnnulmentJob</c> dispara cuando AFIP rechaza la NC.
    /// Transiciona el BC a <c>ArcaRejected</c> y persiste el mensaje de error
    /// AFIP para que el back-office vea el motivo sin abrir Hangfire. Registra
    /// audit <c>BookingCancellationArcaRejected</c>.
    /// </summary>
    Task OnArcaFailedAsync(int originatingInvoiceId, string? afipErrorMessage, CancellationToken ct);
}
