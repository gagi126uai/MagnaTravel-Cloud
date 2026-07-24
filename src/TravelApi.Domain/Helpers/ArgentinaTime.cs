namespace TravelApi.Domain.Helpers;

/// <summary>
/// Helper UNICO (regla T-4) para convertir un instante UTC a la hora de pared de Argentina.
///
/// <para><b>Regla del dueno (dictada, pendiente de numerar en la Constitucion, 2026-07-2x)</b>:
/// TODA fecha de un comprobante fiscal (factura/NC/ND: fecha de emision, vencimiento de pago) y
/// TODA fecha visible para el usuario (voucher, recibo interno) es el DIA CALENDARIO ARGENTINO
/// (America/Argentina/Buenos_Aires), sin importar en que huso horario corre el servidor.</para>
///
/// <para><b>Por que existia el bug</b>: el contenedor de produccion corre con reloj UTC. Un
/// comprobante emitido a las 22hs de Argentina ya es "manana" en UTC (Argentina esta 3 horas
/// atras). Si se formatea la fecha con <c>DateTime.Now</c>/<c>DateTime.UtcNow</c> tal cual, el
/// comprobante sale fechado un dia despues del dia real en que se emitio.</para>
///
/// <para><b>Por que NO usar <c>AddHours(-3)</c> a mano</b>: ese truco asume que Argentina nunca
/// tiene horario de verano. Es cierto HOY (Argentina lo abolio en 2009) pero es un supuesto
/// fragil: si algun dia el pais cambia la ley (paso varias veces en su historia), todo el codigo
/// que resta 3 horas a mano queda mal en silencio, sin que ningun test lo detecte.
/// <see cref="TimeZoneInfo"/> lee la regla real del sistema operativo/base de datos de husos
/// horarios (tz database), asi que si la ley cambia, este helper se actualiza solo con el
/// sistema operativo, sin tocar codigo.</para>
///
/// <para><b>Dos IDs de huso horario</b>: el contenedor de produccion es Linux (usa el ID de la
/// IANA tz database, "America/Argentina/Buenos_Aires"), pero los tests unitarios corren en
/// Windows (que usa su propio catalogo con el ID "Argentina Standard Time"). Probamos el ID de
/// Linux primero y caemos al de Windows si no se encuentra, asi el helper funciona en ambos
/// entornos sin configuracion extra.</para>
/// </summary>
public static class ArgentinaTime
{
    private const string IanaTimeZoneId = "America/Argentina/Buenos_Aires"; // Linux/ICU (contenedor de produccion)
    private const string WindowsTimeZoneId = "Argentina Standard Time"; // Windows (dev local / tests unitarios)

    // Lazy: el TimeZoneInfo se resuelve una sola vez (leer el catalogo del sistema operativo
    // tiene un costo chico pero innecesario de repetir en cada conversion).
    private static readonly Lazy<TimeZoneInfo> ArgentinaTimeZone = new(ResolveArgentinaTimeZone);

    private static TimeZoneInfo ResolveArgentinaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(IanaTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(WindowsTimeZoneId);
        }
    }

    /// <summary>
    /// Convierte un instante UTC a la hora de pared de Argentina. Usar esto para calcular
    /// CUALQUIER fecha/hora que se vaya a mostrar o a mandar a ARCA (fecha de emision de un
    /// comprobante, vencimiento de pago, "emitido el..." de un voucher/recibo, etc).
    ///
    /// <para>Si el <paramref name="utcInstant"/> tiene Kind=Unspecified (por ejemplo viene de un
    /// campo de base de datos que perdio el Kind), se asume UTC — es la convencion que usa todo
    /// el resto del sistema para timestamps de auditoria (CreatedAt, IssuedAt, PaidAt, etc).</para>
    ///
    /// <para><b>NIT N1 (cierre defensivo)</b>: si <paramref name="utcInstant"/> tiene Kind=Local,
    /// NO lo re-etiquetamos como UTC sin mas (eso daria un resultado silenciosamente incorrecto
    /// salvo que el huso local del servidor sea exactamente UTC+0). Lo convertimos primero con
    /// <c>DateTime.ToUniversalTime()</c>, que usa el huso LOCAL real de la maquina para pasar a
    /// UTC antes de aplicar la conversion a Argentina. Hoy nadie del codebase pasa Kind=Local por
    /// este helper (todo el sistema trabaja en UTC o Unspecified-tratado-como-UTC), pero dejamos
    /// la rama cerrada para que si algun dia alguien lo hace, el resultado sea correcto y no un
    /// bug silencioso.</para>
    /// </summary>
    public static DateTime ToArgentinaTime(DateTime utcInstant)
    {
        var utc = utcInstant.Kind switch
        {
            DateTimeKind.Utc => utcInstant,
            DateTimeKind.Local => utcInstant.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc), // Unspecified: convencion del sistema.
        };

        return TimeZoneInfo.ConvertTimeFromUtc(utc, ArgentinaTimeZone.Value);
    }

    /// <summary>Momento actual convertido a hora de pared argentina. Reemplaza a <c>DateTime.Now</c>.</summary>
    public static DateTime GetArgentinaNow() => ToArgentinaTime(DateTime.UtcNow);

    /// <summary>
    /// Dia calendario de HOY en Argentina (sin componente horario, igual que <c>DateTime.Today</c>
    /// pero en huso argentino). Reemplaza a <c>DateTime.Today</c> cuando el dia calendario debe
    /// ser el argentino y no el del servidor.
    /// </summary>
    public static DateTime GetArgentinaToday() => GetArgentinaNow().Date;
}
