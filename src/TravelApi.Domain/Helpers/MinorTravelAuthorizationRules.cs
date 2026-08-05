namespace TravelApi.Domain.Helpers;

/// <summary>
/// Chip "revisar autorización de salida del país" para pasajeros MENORES DE EDAD en un tramo
/// Internacional (decisión firmada del dueño, 2026-08-05, PARTE 3 de la obra "gate ámbito"). Mismo
/// patrón que <see cref="DniExpiryRules"/>/<see cref="PassportExpiryRules"/>: AVISO puro (P-20), nunca
/// candado — jamás bloquea guardar, confirmar ni facturar nada, solo le recuerda al vendedor que lo
/// revise con la familia a tiempo.
///
/// <para><b>Por qué NO reglamenta el trámite</b>: cada país (y cada consulado) pide una autorización
/// distinta para que un menor viaje solo o con un solo progenitor, y las páginas oficiales se
/// contradicen entre sí. Por eso el texto del aviso solo RECUERDA que hay que revisarlo — nunca afirma
/// cuál es el papel exacto que hace falta (eso sería inventar una regla que no está firmada).</para>
///
/// <para><b>Por qué el ámbito SinDefinir NO prende este chip</b> (a diferencia del gate de pasaporte,
/// que sí es conservador con el sin-dato): hoy no existe ningún aviso de menores en la pantalla, así que
/// "sin dato" no está apagando nada que ya estuviera prendido — es un aviso NUEVO, y prenderlo por una
/// reserva sin ámbito cargado sería inventar una alarma que el dueño no pidió.</para>
/// </summary>
public static class MinorTravelAuthorizationRules
{
    /// <summary>Texto único del aviso (un solo nivel, ver <see cref="MinorTravelAlertLevel"/>).</summary>
    public const string RequiresExitAuthorizationCheckWarning =
        "Pasajero menor de edad en un tramo internacional. Revisá si necesita autorización para " +
        "salir del país: el trámite varía según el destino y con quién viaja.";

    /// <summary>
    /// Devuelve el aviso (o null si no corresponde).
    /// </summary>
    /// <param name="birthDate">
    /// Fecha de nacimiento del pasajero. Null = silencio total: sin este dato no se puede calcular la
    /// edad, y no se inventa un aviso a partir de un dato que falta.
    /// </param>
    /// <param name="reservaHasInternationalService">
    /// True si la reserva tiene AL MENOS UN servicio con ámbito Internacional. Sin esto, el aviso no
    /// aplica — no hay tramo fuera del país que exija revisar la autorización de salida.
    /// </param>
    /// <param name="tripStart">Fecha de inicio del viaje (Reserva.StartDate).</param>
    /// <param name="tripEnd">Fecha de fin del viaje (Reserva.EndDate). Si falta, se usa <paramref name="tripStart"/>.</param>
    /// <param name="todayInArgentina">Solo para fijar el "hoy" en los tests (T-14: hora argentina siempre).</param>
    public static MinorTravelAlert? GetAlertOrNull(
        DateTime? birthDate,
        bool reservaHasInternationalService,
        DateTime? tripStart,
        DateTime? tripEnd,
        DateTime? todayInArgentina = null)
    {
        if (!birthDate.HasValue)
        {
            return null;
        }

        if (!reservaHasInternationalService)
        {
            return null;
        }

        var today = (todayInArgentina ?? ArgentinaTime.GetArgentinaToday()).Date;

        // La edad se evalúa contra el FIN del viaje, con fallback al inicio (mismo criterio que
        // PassportExpiryRules/DniExpiryRules — F-1: una sola regla de fechas para toda la pantalla). Sin
        // NINGUNA fecha de viaje cargada, usamos hoy: mejor una referencia aproximada que no poder avisar
        // nunca por falta de fechas en la reserva.
        var referenceDate = (tripEnd ?? tripStart ?? today).Date;

        var ageAtReferenceDate = CalculateAgeAsOf(birthDate.Value.Date, referenceDate);
        if (ageAtReferenceDate >= 18)
        {
            // Ya es mayor de edad para cuando termina el viaje (incluye el borde: si cumple 18 el mismo
            // día que termina el viaje, ya no es menor para ese tramo).
            return null;
        }

        return new MinorTravelAlert(MinorTravelAlertLevel.Notice, RequiresExitAuthorizationCheckWarning);
    }

    /// <summary>
    /// Edad cumplida de <paramref name="birthDate"/> al llegar a <paramref name="asOfDate"/>. No usa
    /// "restar los años" a secas: eso da mal si todavía no llegó el cumpleaños de ese año (ej. nace el
    /// 20/12, la cuenta es el 5/1 del año siguiente: pasaron 0 años calendario completos, no 1).
    /// </summary>
    private static int CalculateAgeAsOf(DateTime birthDate, DateTime asOfDate)
    {
        var age = asOfDate.Year - birthDate.Year;
        if (birthDate.AddYears(age) > asOfDate)
        {
            age--;
        }
        return age;
    }
}

/// <summary>
/// Nivel del aviso de menor en tramo internacional. Un solo valor a propósito (mismo criterio que
/// <see cref="DniAlertLevel"/>): se expone al front como STRING ("Notice"), nunca como número crudo de
/// enum (P-1, T-5).
/// </summary>
public enum MinorTravelAlertLevel
{
    /// <summary>Único nivel: hay que revisar la autorización de salida del país para este menor.</summary>
    Notice,
}

/// <summary>Resultado del aviso: el nivel (para pintar el chip) + el texto exacto que lee el vendedor.</summary>
public sealed record MinorTravelAlert(MinorTravelAlertLevel Level, string Text);
