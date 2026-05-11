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
}
