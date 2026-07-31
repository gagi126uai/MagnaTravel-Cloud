namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 1.
///
/// <para><b>Por que existe</b>: los porcentajes de comision se guardaban sin tope. Un 1000% tipeado de
/// mas (o un -10 con el signo pegado) no rebota en el momento: rebota en la PLATA, cuando el motor
/// devenga la comision del vendedor o calcula el margen de una reserva con ese porcentaje. Un porcentaje
/// negativo o mayor a 100 no existe como comision: o se regala plata o se cobra mas de lo que entro.</para>
/// </summary>
public static class CommissionPercentValidator
{
    /// <summary>
    /// Mensaje criollo unico, igual en la regla de comision por operador y en el % por defecto de la
    /// agencia.
    /// </summary>
    public const string InvalidPercentMessage = "El porcentaje tiene que estar entre 0 y 100.";

    /// <summary>
    /// True si <paramref name="percent"/> esta entre 0 y 100 inclusive. El 0 es VALIDO a proposito:
    /// "sin comision" es una configuracion real (ej. un operador que no paga comision).
    /// </summary>
    public static bool IsValid(decimal percent)
    {
        return percent >= 0m && percent <= 100m;
    }
}
