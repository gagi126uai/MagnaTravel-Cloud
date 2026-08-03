namespace TravelApi.Domain.Reservations;

/// <summary>
/// B3 (obra 2026-07-31 tarde): freno de negocio para no cargar DOS pasajeros con el MISMO documento en
/// la MISMA reserva. Dos personas distintas no pueden compartir un DNI/pasaporte: si el numero se repite
/// dentro de una reserva, o es un error de tipeo, o el vendedor esta cargando dos veces al mismo viajero.
///
/// <para><b>Por que hacia falta este guard nuevo</b>: el unico mecanismo parecido que ya existia era el
/// aviso "quizás te referís a..." de <c>PassengerSearchService</c> — una SUGERENCIA para autocompletar el
/// formulario que busca en TODA la base (cualquier reserva) y nunca bloquea el guardado. Este guard es
/// distinto: compara SOLO contra los pasajeros YA CARGADOS en la MISMA reserva, y SI frena.</para>
///
/// <para><b>Regla de "sospechoso"</b> (decision de esta obra): dos pasajeros de la misma reserva se
/// tratan como duplicados si tienen el MISMO numero de documento Y, ademas, el tipo de documento
/// coincide O el tipo no se conoce de alguno de los dos lados. Con el tipo en blanco no se puede
/// descartar que sea la misma persona (un DNI cargado sin tipo todavia es sospechoso contra un DNI
/// completo) — mejor frenar y que el vendedor confirme a mano, que dejar pasar un duplicado real.</para>
///
/// <para>Clase PURA (sin EF, sin DB), igual que <see cref="PassengerNominalRules"/>: el caller
/// (<c>ReservaService</c>) le pasa los pasajeros ya cargados de la reserva. Este guard vive en el motor,
/// no en la pantalla (T-3): la pantalla puede adelantar el aviso, pero la verdad la impone el backend.</para>
/// </summary>
public static class PassengerDuplicateDocumentGuard
{
    /// <summary>
    /// True si el documento que se quiere cargar (<paramref name="incomingType"/>/
    /// <paramref name="incomingNumber"/>) es sospechoso de ser el MISMO que un pasajero ya cargado
    /// (<paramref name="existingType"/>/<paramref name="existingNumber"/>), segun la regla de la clase.
    /// Los numeros se comparan recortando espacios y sin importar mayusculas/minusculas (mismo criterio
    /// de "no relajar por un espacio de mas" que el resto de esta obra, ver B5).
    /// </summary>
    public static bool IsSuspectedDuplicate(
        string? existingType, string? existingNumber,
        string? incomingType, string? incomingNumber)
    {
        var existingNum = (existingNumber ?? string.Empty).Trim();
        var incomingNum = (incomingNumber ?? string.Empty).Trim();

        // Documento vacio de cualquiera de los dos lados: no hay nada que comparar. Que el documento sea
        // invalido o falte lo frena otro guard (EnsurePassengerDocumentIsValid), no este.
        if (existingNum.Length == 0 || incomingNum.Length == 0)
        {
            return false;
        }

        if (!string.Equals(existingNum, incomingNum, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var existingT = (existingType ?? string.Empty).Trim();
        var incomingT = (incomingType ?? string.Empty).Trim();

        // Mismo numero. Si el tipo de alguno de los dos esta en blanco, no podemos descartar que sea el
        // mismo documento: se trata como sospechoso igual (ver XML doc de la clase).
        if (existingT.Length == 0 || incomingT.Length == 0)
        {
            return true;
        }

        return string.Equals(existingT, incomingT, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mensaje criollo del freno, con el nombre del pasajero YA CARGADO para que el vendedor ubique el
    /// caso sin tener que adivinar. NUNCA repite el numero de documento (dato sensible, mismo criterio
    /// que <c>PassengerNominalRules</c>: el vendedor ya lo tiene tipeado, no hace falta repetirlo).
    /// </summary>
    public static string BuildDuplicateMessage(string? existingPassengerFullName)
    {
        var name = string.IsNullOrWhiteSpace(existingPassengerFullName)
            ? "otro pasajero de esta reserva"
            : existingPassengerFullName.Trim();

        return $"Ya hay un pasajero cargado en esta reserva con el mismo documento ({name}). " +
               "Revisá si es la misma persona antes de cargarlo de nuevo.";
    }
}
