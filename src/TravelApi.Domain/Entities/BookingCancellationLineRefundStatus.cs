namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-025 (DT.1.1, 2026-06-13): estado del circuito de reintegro del OPERADOR de
/// una linea de cancelacion (baja ADR-002 a nivel linea). Es el estado de "que pasa
/// con la plata que esta agencia le pago a ESE operador por el servicio cancelado",
/// distinto del estado fiscal del evento (que vive en el BC padre).
///
/// <para><b>FIX D (2026-07-04) — semantica REAL del campo (leer antes de confiar en el)</b>: quien mueve este
/// valor es <c>AssignRefundCapsAsync</c> (al nacer el circuito, segun el cap de la linea),
/// <c>AllocateConfirmedPenaltyToLinesAsync</c> (lo devuelve a <see cref="None"/> si la multa se comio el cap) y
/// el reparto/reversa del ingreso recibido (<c>OperatorRefundService.Distribute/RemoveReceivedRefund...</c>).
/// El UNICO lector de NEGOCIO es <c>CloseReservaIfOperatorRefundComplete</c>, que solo compara contra
/// <see cref="Settled"/>. Este enum NO se expone en ningun DTO ni pantalla (no hay migracion de datos: los datos
/// viejos con <see cref="None"/> enganoso no llegan al usuario; el cierre por reembolso total no depende de la
/// distincion None/Pending).</para>
/// </summary>
public enum BookingCancellationLineRefundStatus
{
    /// <summary>
    /// No hay reintegro pendiente del operador POR ESTA LINEA. Cubre varios casos reales: no se le pago nada
    /// reembolsable (cap 0), la multa confirmada del operador se comio todo el cap (FIX D), o una imputacion
    /// previa se anulo y la linea quedo sin plata recibida (reversa del ingreso). NO significa "cerrado con exito"
    /// — para eso esta <see cref="Settled"/>.
    /// </summary>
    None = 0,

    /// <summary>
    /// Se le pago al operador y se espera que devuelva (total/anticipo menos su penalidad): la linea tiene
    /// <c>RefundCap &gt; 0</c> todavia sin cubrir. Es el valor con el que nace una linea reembolsable
    /// (FIX D: seteado en <c>AssignRefundCapsAsync</c>).
    /// </summary>
    PendingOperatorRefund = 1,

    /// <summary>El reintegro del operador ya entro y cubrio el cap; se imputo al saldo a favor del cliente.</summary>
    Settled = 2,
}
