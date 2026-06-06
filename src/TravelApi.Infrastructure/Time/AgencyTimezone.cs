namespace TravelApi.Infrastructure.Time;

/// <summary>
/// B1.15 Fase D' (2026-05-11): helper compartido para convertir fechas
/// recibidas del cliente (query string) en el huso horario operativo de la
/// agencia a UTC, evitando el bug recurrente de comparar DateTime con
/// Kind=Unspecified contra columnas <c>timestamp with time zone</c> en
/// Postgres (Npgsql, sin EnableLegacyTimestampBehavior, tira
/// InvalidOperationException -> 500).
///
/// Semantica:
///  - Los DateTime que llegan deserializados desde un query string vienen
///    con <see cref="System.DateTimeKind.Unspecified"/>. Para filtros por
///    "dia", el contrato del front es: "el dia X visto desde la agencia"
///    (no UTC). Por eso interpretamos siempre como hora local de la
///    agencia y convertimos a UTC explicito.
///  - Filtros de rango deben construirse cerrado-abierto [from, to+1day)
///    para capturar todo el dia final local sin perder eventos de la
///    tarde/noche que en UTC caen al dia siguiente.
///
/// NOTA multi-tenant: MagnaTravel hoy opera solo en Argentina (UTC-3 sin
/// DST desde 2009). Si en el futuro hay agencias en otras zonas horarias,
/// esta constante debe parametrizarse por agencia (ej. <c>Agency.Timezone</c>
/// o <c>AgencySettings.Timezone</c>) y resolverse por tenant. La superficie
/// publica de este helper esta pensada para soportar esa migracion sin
/// cambiar los callers (extra overload con offset/zona, manteniendo el
/// default).
/// </summary>
public static class AgencyTimezone
{
    /// <summary>
    /// Offset horario operativo de la agencia. Argentina = UTC - 3h, sin
    /// horario de verano desde 2009.
    /// </summary>
    public static readonly TimeSpan ArgentinaOffset = TimeSpan.FromHours(-3);

    /// <summary>
    /// Convierte una fecha "dia local de la agencia" (DateTime con
    /// Kind=Unspecified, tipicamente proveniente del query string) al
    /// instante UTC equivalente.
    /// </summary>
    /// <param name="localDate">
    /// Fecha local (la parte de hora se descarta).
    /// </param>
    /// <param name="isEndOfDay">
    /// <c>false</c> (default): retorna la medianoche inicial del dia,
    /// adecuada para filtros <c>&gt;=</c> (inclusive).
    /// <c>true</c>: retorna la medianoche del dia siguiente, adecuada para
    /// filtros <c>&lt;</c> (exclusive end). Usar siempre rango
    /// cerrado-abierto [from, to+1day) para no perder eventos del ultimo
    /// dia.
    /// </param>
    /// <returns>
    /// DateTime con <see cref="System.DateTimeKind.Utc"/>.
    /// </returns>
    public static DateTime ToUtcFromAgencyDay(DateTime localDate, bool isEndOfDay = false)
    {
        // .Date descarta hora; sumamos 1 dia si pedimos exclusive end.
        var localMidnight = isEndOfDay ? localDate.Date.AddDays(1) : localDate.Date;
        // Argentina = UTC - 3h -> para llegar a UTC sumamos 3h (restamos el offset negativo).
        return DateTime.SpecifyKind(localMidnight - ArgentinaOffset, DateTimeKind.Utc);
    }

    /// <summary>
    /// ADR-017 F1.4 (§2.2): "hoy" segun el reloj de la agencia (Argentina), como medianoche tageada
    /// <see cref="System.DateTimeKind.Utc"/>. Se usa para decidir "vence hoy / vencio" en las alertas de
    /// fechas limite. Los deadlines se guardan como fecha "de pared" a medianoche con Kind=Utc (sin
    /// conversion); esta funcion devuelve "hoy" con EXACTAMENTE la misma forma, asi la comparacion
    /// (<c>deadline &lt;= hoy + ventana</c>, <c>deadline &lt; hoy</c>) es contra el mismo sistema de
    /// referencia y se traduce a SQL contra columnas <c>timestamp with time zone</c>.
    ///
    /// <para><b>Por que NO <c>DateTime.UtcNow.Date</c></b>: a las 21:00 ART de un dia, en UTC ya es el dia
    /// siguiente; usar la fecha UTC correria el flip a "vencido" 3 horas antes y confundiria al usuario.</para>
    ///
    /// <para><b>Por que offset fijo y NO <c>TimeZoneInfo</c> IANA</b>: se reusa el offset -3 ya definido en
    /// esta clase (Argentina sin horario de verano desde 2009). Da el MISMO resultado que la zona IANA
    /// "America/Argentina/Buenos_Aires" sin depender de que ese id resuelva en el contenedor (un punto de
    /// falla menos en runtime).</para>
    ///
    /// <para><b>CAVEAT (cuando migrar a IANA)</b>: este offset fijo asume que Argentina NO observa horario de
    /// verano (cierto desde 2009). Si Argentina REINSTAURA el horario de verano, o si aparece multi-tenant con
    /// zonas con DST real, este helper deja de ser correcto durante las semanas de DST y hay que migrarlo a
    /// <c>TimeZoneInfo</c> (zona IANA) junto con el resto de la clase (ver nota de clase).</para>
    /// </summary>
    /// <param name="utcNow">
    /// Instante UTC "ahora" (inyectable para tests de borde horario). Si es null se usa
    /// <see cref="System.DateTime.UtcNow"/>.
    /// </param>
    public static DateTime TodayWallClockUtc(DateTime? utcNow = null)
    {
        // (UtcNow + offset negativo) = hora de pared en Argentina; .Date la lleva a la fecha local;
        // SpecifyKind(Utc) la deja con la misma forma que los deadlines guardados (medianoche Kind=Utc).
        var argentinaWallClock = (utcNow ?? DateTime.UtcNow) + ArgentinaOffset;
        return DateTime.SpecifyKind(argentinaWallClock.Date, DateTimeKind.Utc);
    }
}
