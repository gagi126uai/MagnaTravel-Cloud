namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Excepcion de dominio para las validaciones de NEGOCIO del circuito de cobros del CLIENTE (alta, emision
/// de recibo, anulacion de recibo, edicion, anulacion y baja de <c>Payment</c> en <c>PaymentService</c>).
///
/// <para><b>Por que existe</b>: mismo problema que resolvio <c>SupplierPaymentValidationException</c> del
/// lado proveedor (Tanda 1 del plan de remediacion "contrato pantalla-motor", 2026-07-18), pero del lado
/// cliente. Antes <c>PaymentsController</c> atrapaba <see cref="InvalidOperationException"/> "a secas" en
/// cada endpoint y devolvia <c>ex.Message</c> directo al vendedor. Eso funcionaba mientras el service SOLO
/// tirara esa excepcion para rechazos de negocio pensados para el usuario — pero el mismo catch ANCHO
/// tambien atrapa (sin querer) cualquier <see cref="InvalidOperationException"/> real de framework o de una
/// libreria de terceros, que puede traer texto en ingles o nombres de clase/campo internos.</para>
///
/// <para>Con esta excepcion propia, el controller atrapa SOLO <c>PaymentValidationException</c>: si algo
/// revienta con una excepcion "de las comunes" que NO fue pensada para el usuario final, ya NO cae en el
/// catch de negocio, sigue de largo y el <c>GlobalExceptionHandler</c> la convierte en el generico amigable
/// (500), sin exponer nada tecnico.</para>
///
/// <para><b>Por que hereda de <see cref="InvalidOperationException"/></b>: asi cualquier catch existente en
/// el resto del codebase que ya atrape <see cref="InvalidOperationException"/> la sigue atrapando igual.
/// Solo se agrega un catch MAS especifico donde interesa (el controller), sin romper nada que ya dependa
/// del tipo base.</para>
///
/// <para><b>IMPORTANTE</b>: el <c>Message</c> de esta excepcion es SIEMPRE lo que ve el vendedor en
/// pantalla. Nunca debe llevar nombres de clase/campo internos, sufijos tecnicos de <c>ArgumentException</c>,
/// ni texto en ingles: siempre un mensaje de negocio en español, autocontenido.</para>
/// </summary>
public sealed class PaymentValidationException : InvalidOperationException
{
    public PaymentValidationException(string message)
        : base(message)
    {
    }
}
