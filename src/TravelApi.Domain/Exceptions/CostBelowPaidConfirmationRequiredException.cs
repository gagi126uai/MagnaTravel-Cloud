namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Tanda P2 "circuito proveedor" (2026-07-21, decision D2 firmada por Gaston): excepcion de negocio para el
/// guard de <c>BookingService</c> que avisa cuando se edita el costo (<c>NetCost</c>) de un servicio TIPADO
/// y el nuevo costo queda por DEBAJO de lo que ya se le pago al operador por ese servicio puntual.
///
/// <para><b>No es un bloqueo duro</b>: bajar el costo puede ser un descuento real que dio el operador — a
/// diferencia de la familia R1 (<see cref="ServiceCancellationRejectedException"/>, que impide una accion que
/// borra plata de la reserva sin ancla), aca lo que falta es que la persona que edita CONFIRME a propósito
/// que ese excedente va a quedar como saldo a favor con el operador, en vez de guardarse en silencio. El
/// caller reintenta el mismo request con <c>ConfirmCostBelowPaid = true</c> para saltear este aviso.</para>
///
/// <para><b>Por que hereda de <see cref="InvalidOperationException"/></b>: los 5 controllers de servicios
/// tipados (Hotel/Vuelo/Traslado/Paquete/Asistencia) ya tienen un <c>catch (InvalidOperationException ex)</c>
/// que mapea estos rechazos a 409 con <c>{ message = ex.Message }</c>. Heredar de ella hace que ese mismo
/// catch la siga atrapando aunque no se agregue el catch especifico; el controller solo suma la lectura de
/// <see cref="Code"/> cuando el tipo real es este (envelope ADITIVO, mismo patron que
/// <see cref="ServiceCancellationRejectedException"/> y <see cref="OperatorRefundActionRejectedException"/>).</para>
/// </summary>
public sealed class CostBelowPaidConfirmationRequiredException : InvalidOperationException
{
    /// <summary>
    /// Codigos estables de este guard. El frontend los usa como clave para mostrar el cartel de
    /// confirmacion; el backend los define ACA una unica vez para que no puedan divergir entre el throw y
    /// el catch.
    /// </summary>
    public static class Codes
    {
        /// <summary>El costo nuevo del servicio queda por debajo de lo ya pagado al operador y el caller
        /// todavia no confirmo que quiere guardar igual.</summary>
        public const string ConfirmationRequired = "COST_BELOW_PAID_CONFIRMATION_REQUIRED";
    }

    /// <summary>Codigo estable de negocio (ver <see cref="Codes"/>). El front lo usa para mostrar el cartel
    /// de confirmacion — nunca se le muestra crudo al usuario.</summary>
    public string Code { get; }

    public CostBelowPaidConfirmationRequiredException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
