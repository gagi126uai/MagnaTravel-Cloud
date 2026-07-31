namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 2.
///
/// <para><b>Por que existe</b>: la fecha de nacimiento del pasajero decide si viaja como adulto, menor o
/// infante, y va en el listado que se le manda al operador y a la aerolinea. El error real que se veia es
/// de tipeo: el ano de HOY en lugar del de nacimiento (queda una persona que todavia no nacio) o un ano de
/// cuatro digitos mal escrito ("1089"). Las dos cosas pasan desapercibidas hasta el mostrador.</para>
///
/// <para><b>Es candado, no aviso</b> (decision del dueño, 2026-07-31): una fecha de nacimiento futura es
/// IMPOSIBLE, no dudosa. El tope de 120 anos es el borde anti-tipeo: no existe pasajero mas viejo que eso,
/// asi que un ano disparatado queda afuera sin arriesgar rechazar a una persona real.</para>
/// </summary>
public static class BirthDateValidator
{
    /// <summary>Mensaje criollo unico, textual del dueño.</summary>
    public const string InvalidBirthDateMessage = "Esa fecha de nacimiento no puede ser.";

    /// <summary>
    /// Edad maxima que se considera posible. No es un limite de negocio (no hay tope de edad para viajar):
    /// es el borde que separa "persona muy grande" de "ano mal tipeado".
    /// </summary>
    private const int MaximumHumanAgeInYears = 120;

    /// <summary>
    /// True si la fecha de nacimiento es posible. Null = valido (el dato es opcional: hay pasajeros
    /// cargados sin fecha de nacimiento y este gate no viene a exigirla).
    ///
    /// <para><paramref name="todayUtc"/> existe solo para que los tests puedan fijar el "hoy" y no
    /// dependan del reloj de la maquina; en produccion siempre se llama sin ese parametro.</para>
    /// </summary>
    public static bool IsValidOrEmpty(DateTime? birthDate, DateTime? todayUtc = null)
    {
        if (!birthDate.HasValue)
        {
            return true;
        }

        // Se compara solo el DIA (sin hora): la fecha de nacimiento es una fecha de pared, y comparar
        // instantes haria que "hoy" fuera invalido o valido segun la hora del servidor.
        var birthDay = birthDate.Value.Date;
        var today = (todayUtc ?? DateTime.UtcNow).Date;

        if (birthDay > today)
        {
            return false;
        }

        var oldestPossibleBirthDay = today.AddYears(-MaximumHumanAgeInYears);
        return birthDay >= oldestPossibleBirthDay;
    }
}
