namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 1.
///
/// <para><b>Por que existe</b>: el telefono del cliente/pasajero es por donde la agencia lo contacta
/// (WhatsApp, llamada de emergencia en destino). Antes el campo aceptaba cualquier texto: "no tiene",
/// "preguntar a la hermana", "1122334455 (casa) / 1155667788 (cel)". Ese dato despues no sirve para
/// marcar ni para el bot de WhatsApp, y nadie se entera hasta que hay que usarlo.</para>
///
/// <para><b>Que valida</b>: que el campo tenga SOLO un numero de telefono — digitos, con un "+" opcional
/// adelante y separadores de formato comunes (espacios, guiones, parentesis) — y que la cantidad de
/// digitos sea razonable (6 a 15). El 15 no es un numero inventado: es el maximo que define el estandar
/// internacional de numeracion telefonica (E.164), el mismo que usan WhatsApp y los proveedores de SMS.
/// El 6 es el minimo practico de un fijo corto del interior.</para>
///
/// <para><b>Que NO valida</b>: que el numero EXISTA o que tenga la caracteristica correcta del pais
/// (eso pediria una base de numeracion actualizada, fuera de alcance). Solo frena texto que no es un
/// telefono.</para>
/// </summary>
public static class PhoneValidator
{
    /// <summary>
    /// Mensaje criollo unico para el usuario final, igual en todas las puertas (cliente, operador,
    /// pasajero, agencia).
    /// </summary>
    public const string InvalidPhoneMessage =
        "Ese teléfono no parece válido. Cargalo con números (puede empezar con +).";

    /// <summary>Minimo de digitos que aceptamos (fijo corto del interior).</summary>
    private const int MinimumDigits = 6;

    /// <summary>Maximo de digitos del estandar internacional E.164 (el que usa WhatsApp).</summary>
    private const int MaximumDigits = 15;

    /// <summary>
    /// Caracteres de FORMATO que la gente escribe y no cambian el numero: se toleran en cualquier
    /// posicion. El "+" NO esta aca porque tiene una regla propia (solo al principio).
    /// </summary>
    private const string AllowedFormattingCharacters = " -()./";

    /// <summary>
    /// True si <paramref name="rawValue"/> es un telefono razonable. Vacio o nulo se considera VALIDO
    /// a proposito: el telefono es OPCIONAL — este helper solo bloquea un campo con texto que NO es un
    /// telefono, no exige que lo carguen.
    /// </summary>
    public static bool IsValidOrEmpty(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        var trimmed = rawValue.Trim();

        // Paso 1: el "+" del prefijo internacional solo vale UNA vez y SOLO al principio ("+54 11...").
        // Un "+" en el medio ("11+22") es basura, no formato.
        var withoutLeadingPlus = trimmed.StartsWith('+') ? trimmed[1..] : trimmed;
        if (withoutLeadingPlus.Contains('+'))
        {
            return false;
        }

        // Paso 2: de lo que queda, cada caracter tiene que ser un digito o un separador de formato.
        // Aca es donde se caen las letras ("no tiene", "int. 45") y los simbolos raros.
        foreach (var character in withoutLeadingPlus)
        {
            bool isDigit = char.IsDigit(character);
            bool isFormatting = AllowedFormattingCharacters.Contains(character);
            if (!isDigit && !isFormatting)
            {
                return false;
            }
        }

        // Paso 3: largo del numero REAL. Se cuenta con PhoneNormalizer —el mismo normalizador que ya usa
        // el CRM para deduplicar leads— asi "que es un telefono" y "que telefono es" se deciden con la
        // misma cuenta de digitos, sin dos reglas que se puedan desalinear con el tiempo.
        var digitsOnly = PhoneNormalizer.Normalize(withoutLeadingPlus);
        return digitsOnly.Length >= MinimumDigits && digitsOnly.Length <= MaximumDigits;
    }
}
