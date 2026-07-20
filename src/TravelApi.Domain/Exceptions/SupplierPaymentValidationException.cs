namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Tanda 1 del plan de remediacion "contrato pantalla-motor" (2026-07-18): excepcion de dominio para
/// las validaciones de NEGOCIO del circuito de pagos a proveedor (alta, edicion y baja de
/// <c>SupplierPayment</c> en <c>SupplierService</c>).
///
/// <para><b>Por que existe</b>: antes estos rechazos se lanzaban como <see cref="ArgumentException"/> o
/// <see cref="InvalidOperationException"/> "a secas", y el controller las atrapaba con un catch ANCHO de
/// ese mismo tipo para mostrarle el mensaje al vendedor. El problema es que ese catch ancho tambien
/// atrapaba (sin querer) cualquier <see cref="ArgumentException"/>/<see cref="InvalidOperationException"/>
/// que viniera de un bug de framework o de una libreria de terceros — y esos mensajes SI pueden traer
/// texto en ingles, nombres de clase/parametro internos, etc. Con esta excepcion propia, el controller
/// atrapa SOLO <c>SupplierPaymentValidationException</c>: si algo revienta con una excepcion "de las
/// comunes" que NO fue pensada para el usuario final, ya NO cae en el catch de negocio, sigue de largo
/// y el <c>GlobalExceptionHandler</c> la convierte en el generico amigable (500).</para>
///
/// <para><b>Por que hereda de <see cref="InvalidOperationException"/></b>: asi cualquier catch existente
/// en el resto del codebase que ya atrape <see cref="InvalidOperationException"/> (por ejemplo, un catch
/// generico mas arriba en la pila) la sigue atrapando igual. Solo agregamos un catch MAS especifico
/// donde nos interesa (el controller), sin romper nada que ya dependa del tipo base.</para>
///
/// <para><b>IMPORTANTE</b>: el <c>Message</c> de esta excepcion es SIEMPRE lo que ve el vendedor en
/// pantalla. Nunca debe llevar nombres de clase/campo internos (ej. "SupplierPayment.Amount"), ni el
/// sufijo tecnico de <c>ArgumentException</c> ("Parameter 'x'"), ni texto en ingles: siempre un mensaje
/// de negocio en español, autocontenido.</para>
/// </summary>
public sealed class SupplierPaymentValidationException : InvalidOperationException
{
    public SupplierPaymentValidationException(string message)
        : base(message)
    {
    }
}
