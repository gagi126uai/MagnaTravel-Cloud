namespace TravelApi.Domain.Exceptions;

/// <summary>
/// ADR-050 "Volver atras deshace la anulacion entera" (2026-07-24): excepcion de negocio para los rechazos del
/// UNDO de una anulacion (los bloqueos que arma <c>ReservaService.EvaluateCancelledRevertBlockersAsync</c>,
/// tanto la revalidacion de afuera de la transaccion como la revalidacion DENTRO de la transaccion Serializable
/// del undo). Antes viajaban como <see cref="InvalidOperationException"/> pelada y el frontend decidia
/// toast-vs-cartel comparando el LARGO del texto del mensaje (fragil: T-6 pide decidir por codigo estable, no
/// por texto que puede cambiar de largo el dia de mañana).
///
/// <para>A diferencia de <see cref="OperatorRefundActionRejectedException"/> (que distingue varios rechazos
/// con codigos distintos porque el frontend ofrece un boton DIFERENTE segun cual), aca el frontend solo
/// necesita saber "esto es el cartel de deshacer anulacion" — por eso hay un UNICO codigo estable para los
/// tres bloqueos de este camino (NC/saldo/refund ya generado, ND de multa ya emitida, saldo a favor ya
/// consumido en otra reserva).</para>
///
/// <para><b>Por que hereda de <see cref="InvalidOperationException"/></b>: mismo motivo que
/// <see cref="OperatorRefundActionRejectedException"/> — si algun catch nuevo se olvidara del tipo especifico,
/// el catch generico de <c>InvalidOperationException</c> que ya existe en el controller la sigue atrapando
/// (degrada a 409 sin <c>code</c>, nunca rompe el flujo).</para>
/// </summary>
public sealed class UndoAnnulmentBlockedException : InvalidOperationException
{
    /// <summary>
    /// Codigo estable unico de este camino. El texto (<see cref="Exception.Message"/>) es el que ya esta
    /// firmado por el dueño y NO cambia ni una letra; este codigo es ADITIVO para que el frontend deje de
    /// adivinar por el largo del mensaje.
    /// </summary>
    public const string Code = "UNDO_ANNULMENT_BLOCKED";

    public UndoAnnulmentBlockedException(string message) : base(message)
    {
    }
}
