namespace TravelApi.Domain.Helpers;

/// <summary>
/// Textos de tiempo relativo escritos POR EL MOTOR ("en 3 días", "hace 5 meses").
///
/// <para><b>Por que existe</b>: regla T-13 de la constitucion — los numeros y los textos derivados los
/// calcula el servidor, la pantalla no resta fechas. Antes cada pantalla hacia su propia cuenta y dos
/// listas mostraban antigüedades distintas para el mismo dato.</para>
///
/// <para><b>Como se cuenta</b>: siempre en DIAS DE CALENDARIO completos (se comparan fechas, no horas).
/// Asi "mañana" es mañana aunque falten 26 horas, que es como lo lee una persona.</para>
/// </summary>
public static class RelativeDateText
{
    /// <summary>
    /// Cuantos dias de calendario faltan (positivo) o pasaron (negativo) entre <paramref name="today"/>
    /// y <paramref name="date"/>. Ejemplo: sale el 12 y hoy es 9 -> 3.
    /// </summary>
    public static int DaysBetween(DateTime today, DateTime date)
        => (int)(date.Date - today.Date).TotalDays;

    /// <summary>
    /// Cuenta regresiva para una fecha FUTURA (o de hoy/pasada): "hoy", "mañana", "en 3 días",
    /// "en 2 semanas", "en 4 meses", "hace 2 días". Devuelve "" si no hay fecha.
    /// </summary>
    public static string Countdown(DateTime today, DateTime? date)
    {
        if (date is null) return string.Empty;

        var days = DaysBetween(today, date.Value);

        if (days == 0) return "hoy";
        if (days == 1) return "mañana";
        if (days == -1) return "ayer";
        if (days < 0) return $"hace {DescribeSpan(-days)}";
        return $"en {DescribeSpan(days)}";
    }

    /// <summary>
    /// Antigüedad de algo que ya paso: "hoy", "ayer", "hace 5 meses". Devuelve "" si no hay fecha.
    /// Una fecha futura (dato raro) se describe con la cuenta regresiva, no con un "hace -3 días".
    /// </summary>
    public static string Age(DateTime today, DateTime? date)
    {
        if (date is null) return string.Empty;

        var days = DaysBetween(today, date.Value);
        if (days > 0) return Countdown(today, date);
        if (days == 0) return "hoy";
        if (days == -1) return "ayer";
        return $"hace {DescribeSpan(-days)}";
    }

    /// <summary>
    /// Convierte una cantidad de dias en la unidad que usaria una persona. Los cortes son los que se
    /// leen naturales: hasta 13 dias se cuentan dias; hasta 8 semanas, semanas; hasta 24 meses, meses.
    /// </summary>
    private static string DescribeSpan(int days)
    {
        if (days <= 13) return days == 1 ? "1 día" : $"{days} días";

        var weeks = days / 7;
        if (weeks <= 8) return weeks == 1 ? "1 semana" : $"{weeks} semanas";

        // 30 dias por mes: es una aproximacion a proposito, el texto es orientativo.
        var months = days / 30;
        if (months <= 24) return months <= 1 ? "1 mes" : $"{months} meses";

        var years = days / 365;
        return years <= 1 ? "1 año" : $"{years} años";
    }
}
