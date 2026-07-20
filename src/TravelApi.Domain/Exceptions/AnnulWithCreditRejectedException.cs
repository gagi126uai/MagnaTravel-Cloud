namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Tanda 3 del plan "contrato pantalla-motor" (2026-07-20): excepcion de negocio para los cuatro
/// rechazos del camino "Anular reserva con saldo a favor" (<c>POST .../annul-with-credit</c>,
/// <see cref="TravelApi.Infrastructure.Services.ReservaService.AnnulWithPaymentsToCreditAsync"/>) que HOY
/// solo mandaban un <c>message</c> en el body 409, sin ningun identificador que el frontend pudiera usar
/// para mostrar un cartel especifico o un boton de "que hacer ahora" — a diferencia de INV-152/081/100/093
/// (el camino con Nota de Credito), que YA viajan con su codigo en <c>invariantCode</c> porque nacen como
/// <see cref="BusinessInvariantViolationException"/>.
///
/// <para><b>Por que hereda de <see cref="InvalidOperationException"/></b>: el controller
/// (<c>ReservasController.AnnulWithCredit</c>) ya tiene un <c>catch (InvalidOperationException ex)</c> que
/// mapea estos cuatro rechazos a 409 con <c>{ message = ex.Message }</c>. Heredar de ella hace que ese
/// mismo catch la siga atrapando SIN CAMBIAR el status code ni el texto que ya se mostraba; el controller
/// solo agrega la lectura de <see cref="Code"/> cuando el tipo real es este (envelope ADITIVO, Decision C
/// del plan: el <c>message</c> de siempre sigue siendo el default/fallback).</para>
///
/// <para><b>El <c>message</c> NUNCA cambia por usar esta excepcion</b>: es el mismo texto en español que ya
/// veían el vendedor y el administrador antes de esta tanda. Solo se agrega, aparte, un <see cref="Code"/>
/// estable para que el frontend (Tanda 4 de este mismo plan) pueda mapearlo a un mensaje con camino, sin
/// tener que adivinar la causa comparando texto libre.</para>
/// </summary>
public sealed class AnnulWithCreditRejectedException : InvalidOperationException
{
    /// <summary>
    /// Codigos estables de los cuatro rechazos, documentados en la spec UX 2026-07-20 (Tanda 3, tabla "el
    /// mapa codigo -> criollo"). El frontend los usa como clave; el backend los define ACA una unica vez
    /// para que no puedan divergir entre el throw y el catch.
    /// </summary>
    public static class Codes
    {
        /// <summary>La reserva no esta En gestión ni Confirmada (precondicion de estado, paso 3).</summary>
        public const string NotFirmState = "ANNUL_CREDIT_NOT_FIRM_STATE";

        /// <summary>La reserva tiene una factura con CAE vivo: hay que anular por el camino formal con Nota de
        /// Credito, no por este (precondicion fiscal, paso 4).</summary>
        public const string LiveInvoice = "ANNUL_CREDIT_LIVE_INVOICE";

        /// <summary>Hay cobros vivos pero la reserva no tiene un cliente pagador asignado: no hay a quien
        /// acreditarle el saldo a favor (precondicion de pagador, paso 6).</summary>
        public const string NoPayer = "ANNUL_CREDIT_NO_PAYER";

        /// <summary>Freno de plata R1: se le pago al operador por uno o mas servicios y la reserva todavia no
        /// tiene factura para anclar ese reembolso (precondicion de receivable, paso 7,
        /// <see cref="TravelApi.Application.Interfaces.IBookingCancellationService.EnsureReservaAnnulHasReceivableAnchorAsync"/>).</summary>
        public const string UnanchoredOperatorRefund = "ANNUL_CREDIT_UNANCHORED_OPERATOR_REFUND";
    }

    /// <summary>Codigo estable de negocio (ver <see cref="Codes"/>). El front lo usa para elegir el texto
    /// criollo y, si corresponde, el boton de siguiente paso — nunca se le muestra crudo al usuario.</summary>
    public string Code { get; }

    public AnnulWithCreditRejectedException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}
