namespace TravelApi.Domain.Reservations;

/// <summary>
/// Tanda 7 del plan "contrato pantalla-motor" (2026-07-20): los 3 hechos MINIMOS que necesita la ficha para
/// decidir, ANTES de que el usuario clickee la papelera, si anular ESTE servicio va a rebotar. Espejan
/// EXACTAMENTE los 3 candados que ya corren dentro de
/// <c>BookingCancellationService.CancelServiceAsync</c> (voucher, R1 plata pagada al operador sin factura,
/// sin cliente asignado con factura viva) — mismo orden de evaluacion, mismos textos.
/// </summary>
/// <param name="HasLiveVoucher">
/// True si la reserva tiene un voucher emitido vivo (Status Issued). Bloquea CUALQUIER servicio de la
/// reserva por igual (el voucher no es un dato por servicio): mismo criterio EXACTO que
/// <c>MutationGuards.GetReservaVoucherOnlyBlockReasonAsync</c> (el que corre de verdad al anular).
/// </param>
/// <param name="HasLiveSaleInvoiceWithoutPayer">
/// True si la reserva tiene factura de venta viva PERO no tiene un cliente (Payer) asignado. Bloquea
/// CUALQUIER servicio por igual (no hay a quien facturarle la nota de credito): mismo criterio que el
/// candado "sin Payer" de <c>CancelServiceAsync</c>.
/// </param>
/// <param name="HasUnanchoredOperatorRefund">
/// True SOLO para este servicio puntual: tiene plata pagada al operador (su RefundCap reconstruido es mayor
/// a cero) y la reserva NO tiene factura de venta viva que ancle el reembolso.
///
/// <para><b>Obra "anular sin factura" (2026-07-23, decisión del dueño)</b>: este hecho YA NO BLOQUEA
/// (ver <see cref="ServiceCancellationPreflightPolicy.Evaluate"/>) — el candado que representaba
/// (<c>EnsurePaidServiceCancellationHasReceivableAnchorAsync</c>) se eliminó: anular un servicio con plata
/// pagada al operador sin factura ahora SIEMPRE deja la línea-ancla del receivable en vez de rechazar.
/// El parámetro queda (no se borra, para no romper a <c>ServiceCancellationCapabilityStamper</c> ni los
/// tests existentes) pero <c>Evaluate</c> ya no lo mira.</para>
/// </param>
public sealed record ServiceCancellationPreflightContext(
    bool HasLiveVoucher,
    bool HasLiveSaleInvoiceWithoutPayer,
    bool HasUnanchoredOperatorRefund);

/// <summary>
/// Tanda 7: FUENTE UNICA de texto + orden de evaluacion para el pre-chequeo de "anular servicio". Los 3
/// textos son LITERALMENTE los que ya lanza el guard real (<c>BookingCancellationService.CancelServiceAsync</c>);
/// esta clase no inventa redaccion nueva, solo la centraliza para que el pre-chequeo (GET de la ficha) y el
/// candado real (POST de anular) usen la MISMA fuente y no puedan divergir con el tiempo.
///
/// <para><b>Que NO es</b>: NO es la defensa final. El guard real (el que SI toca la base en el momento de
/// anular) sigue siendo quien rechaza la operacion. Si esta politica y el guard discreparan, MANDA el guard
/// — por eso los dos evaluan los MISMOS hechos, y el cross-check contra el guard real corre como test de
/// INTEGRACION Postgres (regla dura de merge del plan, T7).</para>
/// </summary>
public static class ServiceCancellationPreflightPolicy
{
    public const string VoucherBlockedReason =
        "No se puede anular este servicio: la reserva tiene vouchers emitidos. " +
        "Anulá los vouchers primero si necesitás corregir datos.";

    /// <summary>
    /// Obra "anular sin factura" (2026-07-23): este texto QUEDA DEFINIDO (T-6, no romper el code/texto
    /// estable que ya usan tests/front) pero <see cref="Evaluate"/> DEJÓ de usarlo para bloquear — anular
    /// un servicio con plata pagada al operador sin factura ya no rechaza, siempre deja la línea-ancla.
    /// </summary>
    public const string UnanchoredOperatorRefundBlockedReason =
        "No se puede anular este servicio todavía: ya tiene pagos al operador y la reserva aún no tiene " +
        "factura emitida para registrar el reembolso a tu favor. Emití la factura de venta o gestioná el " +
        "reembolso con el operador antes de anular el servicio.";

    /// <summary>
    /// P1 "circuito proveedor" (2026-07-21): candado para BAJAR EL ESTADO de un servicio (de Confirmado a
    /// Solicitado/Cancelado, desde la ficha de edición). A diferencia de anular el servicio (que dejó de
    /// bloquear, ver <see cref="UnanchoredOperatorRefundBlockedReason"/>), bajar el estado SIGUE
    /// bloqueando: esa acción no deja ningún rastro del receivable del operador (no crea una
    /// BookingCancellationLine), a diferencia de anular/cancelar.
    ///
    /// <para>Obra "anular sin factura" (2026-07-23): el texto YA NO pide "emitir factura" (dejó de ser
    /// requisito, decisión del dueño) — ahora orienta a resolver el reembolso con el operador primero.</para>
    /// </summary>
    public const string UnanchoredOperatorRefundBlockedReasonForStatusDowngrade =
        "No se puede bajar el estado de este servicio todavía: ya tiene pagos al operador por esta reserva " +
        "que todavía no están resueltos. Gestioná primero el reembolso con el operador (o cancelá el " +
        "servicio) antes de cambiar su estado.";

    public const string NoPayerBlockedReason =
        "No se puede anular este servicio: la reserva tiene una factura emitida pero no tiene un " +
        "cliente asignado para facturarle la nota de crédito. Asigná un cliente a la reserva antes de anular.";

    /// <summary>
    /// Evalua los candados que TODAVIA aplican a "anular servicio", en el MISMO orden que
    /// <c>CancelServiceAsync</c> los tira: voucher primero (un documento ya entregado, el mas
    /// restrictivo), sin-cliente segundo. Devuelve siempre el PRIMER motivo que aplica — nunca se
    /// muestran dos motivos a la vez para el mismo servicio.
    ///
    /// <para><b>Obra "anular sin factura" (2026-07-23)</b>: <c>ctx.HasUnanchoredOperatorRefund</c> DEJÓ de
    /// evaluarse aca — anular un servicio con plata pagada al operador sin factura ya no se bloquea, deja
    /// la línea-ancla del receivable en su lugar (ver <c>BookingCancellationService.RecordPartialCancellationLineAsync</c>).</para>
    /// </summary>
    public static Cap Evaluate(ServiceCancellationPreflightContext ctx)
    {
        if (ctx.HasLiveVoucher) return Cap.No(VoucherBlockedReason);
        if (ctx.HasLiveSaleInvoiceWithoutPayer) return Cap.No(NoPayerBlockedReason);
        return Cap.Yes;
    }
}
