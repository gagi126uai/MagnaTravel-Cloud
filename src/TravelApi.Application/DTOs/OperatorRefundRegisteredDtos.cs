namespace TravelApi.Application.DTOs;

/// <summary>
/// Tanda P2 "circuito proveedor" (2026-07-22): una fila del listado "reembolsos ya registrados" de UN
/// operador. A diferencia de <see cref="OperatorRefundPendingItemDto"/> (que dice cuanto FALTA cobrarle al
/// operador), esta fila muestra un reembolso QUE YA SE ANOTO como recibido — la pantalla la usa para poder
/// "Deshacer" (<c>DELETE /api/operator-refunds/allocations/{id}</c>) o "Corregir reserva"
/// (<c>PATCH /api/operator-refunds/allocations/{id}/reassociate</c>).
///
/// <para>Una fila = UNA <c>OperatorRefundAllocation</c> (el vinculo de plata entre el ingreso fisico que
/// mando el operador y la cancelacion a la que se imputo esa plata). Se incluyen TANTO las vivas como las
/// deshechas (<see cref="IsVoided"/>): la pantalla las muestra tachadas, no las esconde — es rastro
/// auditable de plata, nunca se borra.</para>
/// </summary>
public class OperatorRefundRegisteredItemDto
{
    /// <summary>PublicId de la imputacion (allocation). Es el identificador que usan los botones "Deshacer" y "Corregir reserva".</summary>
    public Guid PublicId { get; set; }

    /// <summary>
    /// PublicId del ingreso de operador padre (<c>OperatorRefundReceived</c>). Un mismo ingreso fisico
    /// (una transferencia, un cheque) puede cubrir varias reservas a la vez; este dato le permite a la
    /// pantalla agrupar las filas que vinieron del mismo deposito, si hiciera falta.
    /// </summary>
    public Guid RefundReceivedPublicId { get; set; }

    /// <summary>PublicId de la reserva anulada a la que se imputo este reembolso.</summary>
    public Guid ReservaPublicId { get; set; }

    /// <summary>Numero de reserva visible para el usuario (nunca el id interno).</summary>
    public string NumeroReserva { get; set; } = string.Empty;

    /// <summary>Cliente titular de la reserva/cancelacion a la que se imputo este reembolso.</summary>
    public string ClienteNombre { get; set; } = string.Empty;

    /// <summary>
    /// PublicId del cliente titular. Pantalla P2 (2026-07-22): lo usa el boton "Ir a la cuenta del cliente"
    /// que aparece cuando el reembolso no se puede deshacer/reasociar porque el cliente ya gasto ese saldo
    /// (ver <c>OperatorRefundActionRejectedException.Codes.CreditAlreadyUsed</c>).
    /// </summary>
    public Guid ClientePublicId { get; set; }

    /// <summary>Moneda ISO del reembolso (la del ingreso padre: la allocation no tiene columna de moneda propia).</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>Monto NETO imputado a esta cancelacion (lo que efectivamente le quedo como saldo a favor al cliente).</summary>
    public decimal NetAmount { get; set; }

    /// <summary>
    /// true si el usuario que pidio la lista NO tiene <c>cobranzas.see_cost</c>: <see cref="NetAmount"/> viaja
    /// en 0 (la fila se ve igual, solo se oculta el monto). Mismo criterio que el resto de la cuenta del proveedor.
    /// </summary>
    public bool AmountsMasked { get; set; }

    /// <summary>Fecha en que se cargo esta imputacion en el sistema (no la fecha del deposito real del operador).</summary>
    public DateTime RegisteredAt { get; set; }

    /// <summary>
    /// true si esta imputacion fue deshecha (soft-void, boton "Deshacer"). La fila NUNCA se borra: queda
    /// tachada como rastro auditable de que hubo un reembolso mal cargado y se corrigio.
    /// </summary>
    public bool IsVoided { get; set; }

    /// <summary>UTC en que se deshizo. Null mientras la imputacion siga viva.</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>Motivo del deshacer, en criollo (lo escribe quien lo deshace). Null mientras siga viva.</summary>
    public string? VoidedReason { get; set; }
}
