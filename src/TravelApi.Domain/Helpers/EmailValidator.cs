using System.Text.RegularExpressions;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 1.
///
/// <para><b>Por que existe</b>: el mail del cliente, del operador, del pasajero y de la agencia se usan
/// para MANDAR cosas (voucher, factura, aviso de pago). Un mail mal tipeado no rebota en el momento de
/// guardarlo: rebota mucho despues, cuando el envio falla o —peor— cuando nadie se entera de que el
/// cliente nunca recibio su voucher. Este validador frena el dato en la puerta, igual que
/// <see cref="CuitValidator"/> hace con el CUIT.</para>
///
/// <para><b>Que valida y que NO</b>: solo el FORMATO ("algo@algo.algo", sin espacios). NO verifica que
/// la casilla exista ni que reciba mails (eso requeriria mandar un mail de verificacion, fuera de
/// alcance). Es la misma verificacion que hace un formulario web serio antes de aceptar el dato.</para>
/// </summary>
public static class EmailValidator
{
    /// <summary>
    /// Mensaje criollo unico para el usuario final. Centralizado aca para que el vendedor lea SIEMPRE
    /// el mismo texto, venga el rechazo de la ficha del cliente, del operador, del pasajero o de la
    /// configuracion de la agencia.
    /// </summary>
    public const string InvalidEmailMessage = "Ese mail no parece válido. Revisalo.";

    /// <summary>
    /// Patron deliberadamente SIMPLE (sin sobre-ingenieria): algo, arroba, algo, punto, algo.
    ///
    /// <para>Como leerlo de izquierda a derecha:</para>
    /// <list type="bullet">
    ///   <item><c>^[^\s@]+</c>: la parte antes del arroba, uno o mas caracteres que no sean espacio ni arroba.</item>
    ///   <item><c>@</c>: exactamente un arroba (el resto del patron tampoco permite otro).</item>
    ///   <item><c>[^\s@.]+</c>: el primer pedazo del dominio (ej. "gmail"), sin espacios, arrobas ni puntos.</item>
    ///   <item><c>(\.[^\s@.]+)+$</c>: uno o mas pedazos mas, cada uno precedido por un punto (ej. ".com",
    ///     ".com.ar"). Al exigir AL MENOS uno, "juan@gmail" queda afuera y "juan@gmail.com" entra.</item>
    /// </list>
    ///
    /// <para>No intentamos implementar el estandar completo de mails (RFC 5322): ese patron es enorme,
    /// nadie del equipo lo podria leer, y acepta formas que en la practica no existen. Preferimos una
    /// regla legible que atrape los errores REALES de tipeo (falta el arroba, falta el ".com", quedo un
    /// espacio en el medio).</para>
    /// </summary>
    private static readonly Regex EmailPattern = new(
        @"^[^\s@]+@[^\s@.]+(\.[^\s@.]+)+$",
        RegexOptions.Compiled);

    /// <summary>
    /// True si <paramref name="rawValue"/> tiene formato de mail razonable. Un valor vacio o nulo se
    /// considera VALIDO a proposito: el mail es OPCIONAL en clientes, operadores y pasajeros — este
    /// helper solo bloquea un mail MAL ESCRITO, no exige que lo carguen.
    /// </summary>
    public static bool IsValidOrEmpty(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        // Trim: un espacio al final es lo mas comun al pegar desde WhatsApp/Excel y no deberia
        // rebotar el alta entera. Los espacios INTERNOS si rebotan (el patron no los admite).
        return EmailPattern.IsMatch(rawValue.Trim());
    }
}
