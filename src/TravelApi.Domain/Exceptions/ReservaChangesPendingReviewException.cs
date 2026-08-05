namespace TravelApi.Domain.Exceptions;

/// <summary>
/// ADR-043 Fase 1 (2026-08-05, "gate de facturar"): excepcion de negocio para el rechazo de
/// <c>InvoiceService.CreateAsync</c> cuando la reserva tiene <c>HasUnacknowledgedChanges</c> en true (el
/// operador avisó un cambio — precio, servicio caído, servicio nuevo — que todavía nadie revisó con "Dar
/// OK"). Regla T-1 de la constitucion: rechazo tipado con <c>Message</c> en castellano llano + <c>Code</c>
/// estable, para que el frontend no tenga que adivinar la causa comparando texto libre.
///
/// <para><b>Alcance (§8.1 del ADR):</b> el bloqueo es SOLO sobre la <b>factura de venta nueva</b>
/// (<c>CanInvoiceSale</c>). La NC/ND correctiva NO pasa por este guard — es la accion que justamente
/// resuelve el cambio pendiente cuando la reserva ya estaba facturada; bloquearla trabaria ese flujo. Por
/// eso <c>InvoiceService.CreateAsync</c> solo evalua esta excepcion dentro del bloque
/// <c>!request.IsCreditNote &amp;&amp; !request.IsDebitNote</c> (ver ese metodo).</para>
///
/// <para><b>Por que hereda de <see cref="InvalidOperationException"/></b>: el controller
/// (<c>InvoicesController.CreateInvoice</c>) ya tiene un <c>catch (InvalidOperationException ex)</c> que
/// mapea estos rechazos a 409 con <c>{ message = ex.Message }</c>. Heredar de ella hace que ese mismo catch
/// la siga atrapando sin cambiar el status code; el controller solo suma la lectura de <see cref="Code"/>
/// cuando el tipo real es este (envelope ADITIVO, mismo patron que
/// <c>ServiceCancellationRejectedException</c>/<c>OperatorRefundActionRejectedException</c>). El
/// <c>message</c> es literalmente el mismo texto que ya usa <c>ReservaCapabilityPolicy.EvaluateInvoiceSale</c>
/// para apagar el boton en el front (regla T-6: el texto se fija una sola vez, en un solo lugar).</para>
/// </summary>
public sealed class ReservaChangesPendingReviewException : InvalidOperationException
{
    /// <summary>Codigo estable de negocio. El front lo usa como clave (no compara el texto del mensaje).</summary>
    public const string CodeValue = "RESERVA_CAMBIOS_SIN_REVISAR";

    /// <summary>Mismo valor que <see cref="CodeValue"/>, expuesto como propiedad de instancia (T-1).</summary>
    public string Code => CodeValue;

    public ReservaChangesPendingReviewException(string message)
        : base(message)
    {
    }
}
