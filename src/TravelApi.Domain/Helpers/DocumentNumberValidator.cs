namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 2.
///
/// <para><b>Por que existe</b>: el numero de documento del cliente y del pasajero viaja al voucher, al
/// listado de pasajeros que se le manda al operador y —en el caso del cliente— a la factura. Un documento
/// mal cargado (con puntos, con letras de mas, o directamente un texto como "no lo trajo") no se nota al
/// guardarlo: se nota en el aeropuerto o cuando el operador rechaza el pasajero.</para>
///
/// <para><b>Que tan estricto es cada tipo</b> (decision del dueño, 2026-07-31):</para>
/// <list type="bullet">
///   <item><b>DNI</b>: SOLO numeros, 7 u 8 digitos. Es un dato argentino con formato conocido y fijo, asi
///     que se puede exigir. Los puntos NO se aceptan: el sistema guarda el numero limpio (si se guardaran
///     con puntos, dos veces el mismo documento entrarian como dos personas distintas).</item>
///   <item><b>Pasaporte / Cedula / Otro</b>: texto libre razonable. Cada pais tiene su formato (letras,
///     numeros, guiones) y no hay una regla universal; exigir una inventada bloquearia pasajeros reales.
///     Solo se rechaza lo que NO puede ser ningun documento: puro simbolo, o un texto larguisimo.</item>
/// </list>
///
/// <para><b>Vacio = valido</b>, igual que el resto de los validadores de la obra: este gate frena un dato
/// MAL cargado, no exige que el dato exista (hay reservas donde el documento se completa despues).</para>
/// </summary>
public static class DocumentNumberValidator
{
    /// <summary>Mensaje criollo unico cuando el tipo elegido es DNI y el numero no cierra.</summary>
    public const string InvalidDniMessage = "Ese DNI no parece válido: son 7 u 8 números, sin puntos.";

    /// <summary>
    /// Mensaje para los tipos de texto libre (Pasaporte / Cedula / Otro). Solo aparece cuando lo cargado
    /// no puede ser un documento de ningun pais (ej. "???" o un parrafo entero).
    /// </summary>
    public const string InvalidDocumentNumberMessage = "Ese número de documento no parece válido. Revisalo.";

    private const int MinimumDniDigits = 7;
    private const int MaximumDniDigits = 8;

    /// <summary>
    /// Tope de largo para los documentos de texto libre. Un pasaporte real no pasa de ~10 caracteres; 20
    /// deja margen de sobra para formatos raros y corta el caso "escribieron una frase en el casillero".
    /// </summary>
    private const int MaximumFreeFormLength = 20;

    /// <summary>
    /// True si el tipo elegido es DNI (el unico con formato exigible). Se compara sin distinguir
    /// mayusculas y sacando puntos/espacios, asi "dni", "DNI" y "D.N.I." son el mismo tipo.
    /// </summary>
    public static bool IsDniType(string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
        {
            return false;
        }

        var normalized = documentType
            .Replace(".", string.Empty)
            .Replace(" ", string.Empty)
            .Trim();

        return string.Equals(normalized, "DNI", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True si el numero de documento es aceptable PARA EL TIPO elegido. Vacio = valido.
    /// </summary>
    public static bool IsValidOrEmpty(string? documentType, string? documentNumber)
    {
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            return true;
        }

        var trimmed = documentNumber.Trim();

        return IsDniType(documentType)
            ? IsValidDni(trimmed)
            : IsPlausibleFreeFormDocument(trimmed);
    }

    /// <summary>
    /// Devuelve el mensaje que le corresponde al tipo elegido, para que quien llama no tenga que decidirlo.
    /// El del DNI es puntual (dice cuantos numeros van); el de los demas es generico a proposito, porque
    /// no hay un formato unico que se pueda explicar.
    /// </summary>
    public static string MessageFor(string? documentType)
    {
        return IsDniType(documentType) ? InvalidDniMessage : InvalidDocumentNumberMessage;
    }

    private static bool IsValidDni(string trimmedNumber)
    {
        if (trimmedNumber.Length < MinimumDniDigits || trimmedNumber.Length > MaximumDniDigits)
        {
            return false;
        }

        foreach (var character in trimmedNumber)
        {
            if (!char.IsDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Chequeo flojo para Pasaporte / Cedula / Otro: tiene que entrar en el largo maximo, tener al menos
    /// una letra o numero, y no traer caracteres que ningun documento usa. Se admiten los separadores de
    /// formato que si aparecen en documentos extranjeros (guion, punto, barra, espacio).
    /// </summary>
    private static bool IsPlausibleFreeFormDocument(string trimmedNumber)
    {
        if (trimmedNumber.Length > MaximumFreeFormLength)
        {
            return false;
        }

        var hasLetterOrDigit = false;

        foreach (var character in trimmedNumber)
        {
            if (char.IsLetterOrDigit(character))
            {
                hasLetterOrDigit = true;
                continue;
            }

            var isFormattingSeparator = character is '-' or '.' or '/' or ' ';
            if (!isFormattingSeparator)
            {
                return false;
            }
        }

        return hasLetterOrDigit;
    }
}
