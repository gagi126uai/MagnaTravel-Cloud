namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Decisión firmada 2026-08-19 ("descalce devolución-caja y anulación a medias", punto 2): un movimiento
/// de caja que ES la devolución que un operador mandó por una cancelación NO se edita ni se borra desde
/// Tesorería. Si se corrigiera desde ahí, la caja y el circuito de la anulación (que "Deshacer" en la
/// ficha del operador mantiene sincronizados) se desincronizarían de nuevo — exactamente el problema que
/// esta obra vino a resolver. El guard vive en el SERVIDOR (T-10): aunque el frontend apague los botones
/// de Editar/Anular para estos movimientos, un POST/PUT directo a la API también se frena acá.
///
/// <para><b>Por qué hereda de <see cref="InvalidOperationException"/></b>: mismo motivo que
/// <see cref="OperatorRefundActionRejectedException"/>/<see cref="UndoAnnulmentBlockedException"/> — si algún
/// catch nuevo se olvidara del tipo específico, el catch genérico de <c>InvalidOperationException</c> que ya
/// existe en <c>TreasuryController</c> (en <c>UpdateManualMovement</c> Y en <c>DeleteManualMovement</c>, mismo
/// mensaje sanitizado fijo en los dos — paridad agregada 2026-08-19) la sigue atrapando: degrada a 400 sin
/// <c>code</c>, nunca rompe el flujo ni expone un mensaje técnico.</para>
/// </summary>
public sealed class CashMovementLinkedToOperatorRefundException : InvalidOperationException
{
    /// <summary>Código estable de negocio (T-1). El frontend lo usa para distinguir este rechazo puntual sin
    /// tener que comparar el texto del mensaje — nunca se le muestra crudo al usuario.</summary>
    public const string Code = "CASH_MOVEMENT_LINKED_TO_OPERATOR_REFUND";

    public CashMovementLinkedToOperatorRefundException(string? numeroReserva)
        : base(BuildMessage(numeroReserva))
    {
    }

    /// <summary>
    /// Mismo texto EXACTO firmado en la spec (T-6: se fija en tests). Caso defensivo sin número de reserva
    /// (dato roto/legacy — no debería pasar en la operatoria normal, ver <c>TreasuryService</c>): mensaje
    /// genérico, sin inventar un número que no tenemos.
    /// </summary>
    private static string BuildMessage(string? numeroReserva) =>
        string.IsNullOrWhiteSpace(numeroReserva)
            ? "Este movimiento es la devolución del operador de una reserva. Para corregirlo, deshacé la " +
              "devolución desde la ficha del operador — así la caja y la anulación quedan coherentes."
            : $"Este movimiento es la devolución del operador de la reserva {numeroReserva}. Para corregirlo, " +
              "deshacé la devolución desde la ficha del operador — así la caja y la anulación quedan coherentes.";
}
