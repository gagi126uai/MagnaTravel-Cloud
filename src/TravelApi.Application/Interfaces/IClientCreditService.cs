using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.2.3 v3 §2.3 (2026-05-18): contrato del modulo que gestiona el saldo a
/// favor del cliente y los retiros (T3 del flujo de cancelacion/refund).
///
/// <para>
/// <b>Responsabilidades</b>:
/// <list type="bullet">
///   <item><see cref="CreateEntryAsync"/>: crear el saldo cuando
///         <c>OperatorRefundService.AllocateAsync</c> imputa una allocation
///         contra una BC.</item>
///   <item><see cref="WithdrawAsync"/>: ejecutar un retiro del saldo
///         (efectivo, transferencia, "lo dejo como credito", aplicar a otra
///         reserva, o devolver al operador).</item>
///   <item>Queries para que la UI muestre saldo + timeline.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Cierre del BC</b>: cuando el ultimo withdraw deja TODOS los entries del BC
/// con <c>RemainingBalance == 0</c>, este service llama a
/// <c>IBookingCancellationService.OnAllCreditConsumedAsync</c> para que el BC
/// pase a <c>Closed</c> y la Reserva a <c>Cancelled</c>. El service NO toca el
/// BC directamente — respeta el aggregate boundary del modulo BC.
/// </para>
///
/// <para>
/// <b>Por que se separa de OperatorRefundService</b>: aunque ambos tocan el
/// mismo grafo de entidades, las responsabilidades fiscales son distintas. El
/// refund vive en el aggregate del operador (Supplier); el credit vive en el
/// del cliente (Customer). Separarlos permite que B-IMP-1..5 puedan auditar
/// flujos del cliente sin tocar la logica del operador.
/// </para>
/// </summary>
public interface IClientCreditService
{
    /// <summary>
    /// Crea una <see cref="ClientCreditEntry"/> con saldo inicial igual a
    /// <paramref name="netAmount"/> (el cliente puede retirar ese monto desde
    /// FC1.2.3 en adelante).
    ///
    /// <para>
    /// <b>Llamado solo desde infraestructura</b>: NO expuesto via API publica.
    /// El caller actual es <c>OperatorRefundService.AllocateAsync</c>; en futuras
    /// FCs se puede sumar callers para creditos manuales del admin (out of
    /// scope FC1.2).
    /// </para>
    ///
    /// <para>
    /// <b>Contrato del caller</b>: el entry se agrega al ChangeTracker via
    /// <c>_db.Add</c> pero <b>NO</b> se commitea aca — el caller hace el
    /// <c>SaveChangesAsync</c> envolvente para mantener atomicidad con la
    /// allocation y los demas side-effects (HC1 plan v3).
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Por que recibimos la entidad <see cref="OperatorRefundAllocation"/> en
    /// vez del Id int</b>: este metodo se invoca DENTRO de la misma unidad de
    /// trabajo donde la allocation recien fue <c>Add()</c>-eada al ChangeTracker
    /// pero todavia NO se persistio (HC1 plan v3: un solo SaveChanges al final).
    /// En ese momento <c>allocation.Id == 0</c> — si pasamos el Id directo, EF
    /// escribe 0 en la columna FK y Postgres rechaza el INSERT con violacion FK.
    /// Pasando la entidad, seteamos la navigation property y EF resuelve la FK
    /// al hacer SaveChanges en orden topologico (primero el padre, despues el
    /// hijo con el Id ya generado).
    /// </remarks>
    Task<ClientCreditEntry> CreateEntryAsync(
        int bookingCancellationId,
        OperatorRefundAllocation operatorRefundAllocation,
        int customerId,
        decimal netAmount,
        string currency,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// FC1.2.3 (2026-05-18): retira saldo de un <see cref="ClientCreditEntry"/>
    /// segun el <see cref="WithdrawalKind"/> elegido. Es la entrada publica del
    /// flujo T3 — un controller delega aca cuando el cashier o el cliente
    /// pidieron "retirar mi saldo a favor".
    ///
    /// <para>
    /// <b>Kinds y side-effects</b>:
    /// <list type="bullet">
    ///   <item><c>KeptAsCredit</c>: solo deja huella de la decision
    ///         (Amount=0, sin egreso de caja). El saldo sigue vivo y reusable.</item>
    ///   <item><c>PhysicalCash</c> / <c>Transfer</c>: decrementa
    ///         <c>RemainingBalance</c>, genera un <see cref="ManualCashMovement"/>
    ///         Expense (egreso de caja).</item>
    ///   <item><c>AppliedToNewBooking</c>: decrementa
    ///         <c>RemainingBalance</c>, NO genera ManualCashMovement (el
    ///         PaymentService lo hara al imputar el pago en la nueva reserva).</item>
    ///   <item><c>ReversedToOperator</c>: requiere approval
    ///         <c>ClientRefundReversal</c> + audit reforzado. Decrementa el
    ///         <c>RemainingBalance</c> y genera ManualCashMovement Income
    ///         (la plata vuelve a caja porque el cliente la devolvio).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Cierre del BC en cascada</b>: cuando este retiro deja el BC con TODOS
    /// los entries en <c>RemainingBalance == 0</c>, el service notifica a
    /// <c>IBookingCancellationService.OnAllCreditConsumedAsync</c> para que el
    /// BC pase a <c>Closed</c> y la Reserva a <c>Cancelled</c> en la misma
    /// transaccion (HC1 plan v3: un solo SaveChanges al final).
    /// </para>
    ///
    /// <para>
    /// <b>Invariantes</b> (validados por el service):
    /// <list type="bullet">
    ///   <item>INV-085: <c>amount &lt;= entry.RemainingBalance</c> (CHECK SQL
    ///         tambien lo enforca).</item>
    ///   <item>INV-094: Ley 25.345 — efectivo no puede superar el threshold.</item>
    /// </list>
    /// </para>
    /// </summary>
    Task<ClientCreditWithdrawalDto> WithdrawAsync(
        Guid entryPublicId,
        WithdrawClientCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// FC1.2.3: obtiene un <see cref="ClientCreditEntry"/> con sus retiros
    /// (timeline) por PublicId. Null si no existe.
    /// </summary>
    Task<ClientCreditEntryDto?> GetEntryByPublicIdAsync(
        Guid publicId,
        CancellationToken ct);

    /// <summary>
    /// FC1.2.3: lista los entries asociados a un BC (puede haber N por N retiros
    /// del operador). Ordenado por CreatedAt asc. Lista vacia si no hay entries.
    /// </summary>
    Task<List<ClientCreditEntryDto>> GetEntriesByBcAsync(
        Guid bookingCancellationPublicId,
        CancellationToken ct);
}
