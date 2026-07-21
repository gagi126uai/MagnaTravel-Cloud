namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Pantalla P2 "deshacer/reasociar reembolso del operador" (2026-07-22): excepcion de negocio para los
/// rechazos de <c>OperatorRefundService.VoidAllocationAsync</c> y <c>ReassociateAllocationAsync</c> que hoy
/// solo mandaban un <c>message</c> en el body 409, sin ningun identificador que el frontend pudiera usar
/// para elegir el boton de siguiente paso correcto (regla de la casa: el frontend NUNCA adivina la causa
/// comparando el texto del mensaje — mismo criterio que <see cref="ServiceCancellationRejectedException"/>
/// para "anular servicio").
///
/// <para><b>Por que hereda de <see cref="InvalidOperationException"/></b>: el controller
/// (<c>OperatorRefundsController.VoidAllocation</c> / <c>Reassociate</c>) ya tiene un
/// <c>catch (InvalidOperationException ex)</c> que mapea estos rechazos a 409 con <c>{ message = ex.Message }</c>.
/// Heredar de ella hace que ese mismo catch la siga atrapando si en algun punto no se agrega el catch
/// especifico; el controller solo suma la lectura de <see cref="Code"/> cuando el tipo real es este
/// (envelope ADITIVO, el <c>message</c> nunca cambia).</para>
/// </summary>
public sealed class OperatorRefundActionRejectedException : InvalidOperationException
{
    /// <summary>
    /// Codigos estables de los rechazos de esta pantalla. El frontend los usa como clave; el backend los
    /// define ACA una unica vez para que no puedan divergir entre el throw y el catch.
    /// </summary>
    public static class Codes
    {
        /// <summary>
        /// El saldo a favor que genero este reembolso ya fue retirado (efectivo/transferencia) o aplicado a
        /// otra reserva por el cliente: no se puede simplemente deshacer/reasociar esa plata sin revertir
        /// primero ese uso. El frontend ofrece el boton "Ir a la cuenta del cliente" para ese caso.
        /// </summary>
        public const string CreditAlreadyUsed = "REFUND_CREDIT_ALREADY_USED";
    }

    /// <summary>Codigo estable de negocio (ver <see cref="Codes"/>). El front lo usa para elegir el boton de
    /// siguiente paso — nunca se le muestra crudo al usuario.</summary>
    public string Code { get; }

    public OperatorRefundActionRejectedException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
