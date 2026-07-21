namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Tanda 7 del plan "contrato pantalla-motor" (2026-07-20): excepcion de negocio para los tres rechazos de
/// <c>BookingCancellationService.CancelServiceAsync</c> (anular UN servicio) que hoy solo mandaban un
/// <c>message</c> en el body 409, sin ningun identificador que el frontend pudiera usar para elegir el
/// cartel/boton correcto — a diferencia del camino "Anular reserva con saldo a favor" (Tanda 3), que ya
/// viaja con <see cref="AnnulWithCreditRejectedException"/>.
///
/// <para><b>Por que hereda de <see cref="InvalidOperationException"/></b>: el controller
/// (<c>CancellationsController.CancelService</c>) ya tiene un <c>catch (InvalidOperationException ex)</c>
/// que mapea estos tres rechazos a 409 con <c>{ message = ex.Message }</c> (via <c>SanitizedConflict</c>).
/// Heredar de ella hace que ese mismo catch la siga atrapando SIN CAMBIAR el status code ni el texto que ya
/// se mostraba; el controller solo agrega la lectura de <see cref="Code"/> cuando el tipo real es este
/// (envelope ADITIVO, mismo patron que <see cref="AnnulWithCreditRejectedException"/>).</para>
///
/// <para><b>El <c>message</c> NUNCA cambia por usar esta excepcion</b>: los tres textos son LITERALMENTE
/// los mismos que ya veia el vendedor antes de esta tanda (ver
/// <see cref="TravelApi.Domain.Reservations.ServiceCancellationPreflightPolicy"/>, la fuente unica de esos
/// textos). Solo se agrega, aparte, un <see cref="Code"/> estable para que el frontend pueda mapearlo a un
/// cartel con el camino correcto (ej. boton "Emitir factura" para R1), sin tener que adivinar la causa
/// comparando texto libre.</para>
/// </summary>
public sealed class ServiceCancellationRejectedException : InvalidOperationException
{
    /// <summary>
    /// Codigos estables de los tres rechazos de <c>CancelServiceAsync</c>. El frontend los usa como clave;
    /// el backend los define ACA una unica vez para que no puedan divergir entre el throw y el catch.
    /// </summary>
    public static class Codes
    {
        /// <summary>La reserva tiene un voucher Issued vivo: un documento ya entregado al cliente no se reescribe.</summary>
        public const string VoucherLive = "CANCEL_SERVICE_VOUCHER_LIVE";

        /// <summary>Freno de plata R1: el servicio tiene pagos al operador sin factura de venta viva que ancle
        /// el reembolso.</summary>
        public const string UnanchoredOperatorRefund = "CANCEL_SERVICE_UNANCHORED_OPERATOR_REFUND";

        /// <summary>La reserva tiene factura de venta viva pero no tiene un cliente (Payer) asignado: no hay a
        /// quien facturarle la nota de credito.</summary>
        public const string NoPayer = "CANCEL_SERVICE_NO_PAYER";
    }

    /// <summary>Codigo estable de negocio (ver <see cref="Codes"/>). El front lo usa para elegir el cartel y,
    /// si corresponde, el boton de siguiente paso — nunca se le muestra crudo al usuario.</summary>
    public string Code { get; }

    public ServiceCancellationRejectedException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
