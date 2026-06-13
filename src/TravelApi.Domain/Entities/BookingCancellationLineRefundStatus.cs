namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-025 (DT.1.1, 2026-06-13): estado del circuito de reintegro del OPERADOR de
/// una linea de cancelacion (baja ADR-002 a nivel linea). Es el estado de "que pasa
/// con la plata que esta agencia le pago a ESE operador por el servicio cancelado",
/// distinto del estado fiscal del evento (que vive en el BC padre).
/// </summary>
public enum BookingCancellationLineRefundStatus
{
    /// <summary>No hay reintegro pendiente: no se le pago nada al operador, o ya se cerro.</summary>
    None = 0,

    /// <summary>Se le pago al operador y se espera que devuelva (total/anticipo menos su penalidad).</summary>
    PendingOperatorRefund = 1,

    /// <summary>El reintegro del operador ya entro y se imputo al saldo a favor del cliente.</summary>
    Settled = 2,
}
