namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Regla nueva del dueño (2026-08-06), refina la decisión 2026-06-17 ("agregar pasajero = completar el
/// roster, no pide autorización bajo candado"). Esa decisión sigue INTACTA mientras falte algún pasajero
/// de los declarados: completar un lugar vacío nunca pidió permiso y sigue sin pedirlo. El agujero que
/// esta excepción cierra es distinto: con la reserva Confirmada SIN autorización de edición viva (candado
/// activo) y los N pasajeros declarados YA TODOS cargados, agregar uno más deja de ser "completar" — no
/// hay ningún lugar vacío que llenar — y pasa a ser "alterar" la reserva, exactamente lo que el candado
/// existe para frenar (mismo criterio que ya frena Editar/Borrar de un pasajero ya cargado).
///
/// <para>Regla T-1 de la constitución: rechazo tipado con <c>Message</c> en castellano llano + <c>Code</c>
/// estable, para que el frontend no tenga que adivinar la causa comparando texto libre — necesita el Code
/// para pintar el botón "Agregar Pasajero" travado con candadito ANTES de que el vendedor intente guardar,
/// igual que ya hace con Editar/Borrar de un pasajero (candado C1, spec 2026-07-22).</para>
///
/// <para>Hereda de <see cref="InvalidOperationException"/> por el mismo motivo que
/// <c>ReservaChangesPendingReviewException</c>: el controller (<c>ReservasController.AddPassenger</c>) ya
/// tiene un <c>catch (InvalidOperationException ex)</c> que mapea estos rechazos a 409 con
/// <c>{ message = ex.Message }</c>. Heredar de ella hace que ese mismo catch la siga atrapando sin cambiar
/// el status code; el controller solo suma la lectura de <see cref="Code"/> cuando el tipo real es este
/// (envelope ADITIVO, mismo patrón que <c>ReservaChangesPendingReviewException</c>).</para>
/// </summary>
public sealed class PassengerRosterCompleteUnderLockException : InvalidOperationException
{
    /// <summary>Código estable de negocio. El front lo usa como clave (no compara el texto del mensaje).</summary>
    public const string CodeValue = "RESERVA_PASAJEROS_COMPLETOS_BAJO_CANDADO";

    /// <summary>Mismo valor que <see cref="CodeValue"/>, expuesto como propiedad de instancia (T-1).</summary>
    public string Code => CodeValue;

    public PassengerRosterCompleteUnderLockException(string message)
        : base(message)
    {
    }
}
