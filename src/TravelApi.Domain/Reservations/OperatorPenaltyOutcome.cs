namespace TravelApi.Domain.Reservations;

/// <summary>
/// Fase A (2026-06-28): RESULTADO (estado de resolucion) de la pata "multa del operador" de la cancelacion
/// vigente de una reserva, leido al armar el DETALLE. Es informativo: NO es una capacidad de accion, le dice
/// al front en que punto esta la penalidad del operador para mostrar el cartel correcto al CARGAR la ficha,
/// sin tener que pedir aparte el detalle de la cancelacion.
///
/// <para>Existe porque el codigo de resolucion (<c>OperatorPenaltyWaived</c>) hoy solo vivia en el DTO de la
/// cancelacion (<c>GET /cancellations/by-reserva</c>), que la ficha de la reserva no lee al cargar. Surface-arlo
/// en la reserva permite pintar "Cerrada sin multa del operador" sin un round-trip extra.</para>
///
/// <list type="bullet">
/// <item><see cref="None"/>: la reserva no tiene cancelacion vigente, o su pata de operador no esta en juego.</item>
/// <item><see cref="Pending"/>: hay una multa del operador PENDIENTE de resolver (confirmar la multa o cerrar sin multa).</item>
/// <item><see cref="Confirmed"/>: la multa se confirmo (se emitio/encolo la Nota de Debito pass-through).</item>
/// <item><see cref="Waived"/>: se cerro SIN multa (el operador no cobro penalidad; no hay Nota de Debito).</item>
/// </list>
/// </summary>
public enum OperatorPenaltyOutcome
{
    None = 0,
    Pending = 1,
    Confirmed = 2,
    Waived = 3,
}
