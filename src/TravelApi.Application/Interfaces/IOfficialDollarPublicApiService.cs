namespace TravelApi.Application.Interfaces;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "hallazgo del dueño en vivo" + "el dolar nunca falta"): respaldo
/// REAL del dolar oficial via VARIAS APIs publicas, para cuando ARCA no sirve un numero util.
/// Motivo concreto: en homologacion, <c>FEParamGetCotizacion</c> devuelve cotizaciones de JUGUETE
/// (verificado 2026-08-05: dio 1152.202 cuando el oficial real de ese dia rondaba 1496) — sin este
/// respaldo, el dashboard mostraba un numero falso o quedaba mudo.
///
/// <para><b>Cada metodo es UN proveedor distinto, contrato verificado con <c>curl</c> real el
/// 2026-08-05</b> (nunca se asumio la forma de una respuesta):</para>
/// <list type="bullet">
///   <item><see cref="GetTodayRateAsync"/> — dolarapi.com.</item>
///   <item><see cref="GetTodayRateFromMonedApiAsync"/> — monedapi.ar, BNA especifico.</item>
///   <item><see cref="GetTodayRateFromCriptoYaAsync"/> — criptoya.com, banco BNA dentro del listado.</item>
///   <item><see cref="GetRateForDateAsync"/> — argentinadatos.com, sirve tanto HOY como fechas pasadas
///   (unico de los cinco con historial real).</item>
///   <item><see cref="GetTodayRateFromBluelyticsAsync"/> — bluelytics.com.ar, PROMEDIO de mercado
///   (no es el BNA puntual) — por eso va ULTIMO en la escalera de <c>ExchangeRateSyncJob</c>.</item>
/// </list>
///
/// <para><b>Solo lo usa <see cref="ExchangeRateSyncJob"/></b> (mismo criterio que
/// <see cref="IBnaExchangeRateService"/>): el camino interactivo de las pantallas nunca le pega a una
/// API externa en vivo, solo lee lo que el job diario (o el disparo on-demand del resolver, que
/// tambien encola ese mismo job) ya dejo guardado en <c>ExchangeRateQuotes</c>
/// (via <see cref="IExchangeRateResolver"/>).</para>
///
/// <para><b>Nunca tira una excepcion hacia afuera</b> (mismo criterio T-12 que el resto de la
/// escalera de tipo de cambio): cualquier falla de red, timeout, o respuesta con forma inesperada se
/// atrapa adentro y devuelve <c>null</c> — el job decide que hacer con eso (seguir con el proximo
/// respaldo, o dejar el hueco para la proxima corrida).</para>
/// </summary>
public interface IOfficialDollarPublicApiService
{
    /// <summary>
    /// Cotizacion de HOY desde dolarapi.com (<c>GET /v1/dolares/oficial</c>). Usa el campo
    /// <c>venta</c> (lo que el cliente paga), consistente con el resto de la escalera (ARCA
    /// <c>MonCotiz</c>, BNA vendedor). Primer intento de la escalera (T-12, proveedor mas estable
    /// verificado historicamente).
    /// </summary>
    Task<PublicDollarRateReading?> GetTodayRateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cotizacion de HOY desde monedapi.ar (<c>GET /api/v2/usd/bna</c>), candidato ESTRELLA porque
    /// es el unico de los cinco que pide el dolar BNA especificamente por nombre (no "el oficial
    /// generico"). Usa el campo <c>sell</c> del JSON (equivalente a "venta" en los demas).
    /// </summary>
    Task<PublicDollarRateReading?> GetTodayRateFromMonedApiAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cotizacion de HOY desde criptoya.com (<c>GET /api/bancostodos</c>), tomando puntualmente la
    /// clave <c>"bna"</c> del listado de bancos que trae la respuesta (el endpoint sirve TODOS los
    /// bancos en una sola llamada; este servicio descarta el resto). Usa el campo <c>ask</c> (lo que
    /// el banco pide para VENDER — equivalente a "venta"/"sell" en los demas proveedores).
    /// </summary>
    Task<PublicDollarRateReading?> GetTodayRateFromCriptoYaAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cotizacion desde argentinadatos.com para <paramref name="date"/>
    /// (<c>GET /v1/cotizaciones/dolares/oficial/{yyyy}/{MM}/{dd}</c>). A diferencia de los otros
    /// cuatro proveedores (que solo saben dar el dato de "ahora"), este endpoint por-fecha SIRVE
    /// TANTO para HOY como para el backfill de dias pasados (verificado con <c>curl</c> el
    /// 2026-08-05: la fecha de hoy ya esta publicada en el momento en que corre el job). Devuelve
    /// <c>null</c> si la API no tiene fila para esa fecha (404).
    /// </summary>
    Task<PublicDollarRateReading?> GetRateForDateAsync(DateOnly date, CancellationToken cancellationToken);

    /// <summary>
    /// Cotizacion de HOY desde bluelytics.com.ar (<c>GET /v2/latest</c>), campo
    /// <c>oficial.value_sell</c>. <b>OJO — este numero es un PROMEDIO de mercado, no el dolar puntual
    /// de Banco Nacion</b> (aunque en la practica suele salir muy cercano al BNA, porque BNA es una
    /// de las referencias que promedia). Por eso es el ULTIMO de las cinco APIs en la escalera del
    /// job: solo se usa si dolarapi, monedapi, criptoya Y argentinadatos fallaron los cuatro.
    /// </summary>
    Task<PublicDollarRateReading?> GetTodayRateFromBluelyticsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Lectura minima de una de las cinco APIs publicas. <see cref="ProviderName"/> identifica CUAL
/// respondio ("dolarapi" / "monedapi" / "criptoya" / "argentinadatos" / "bluelytics") — es lo que
/// termina en <c>ExchangeRateQuote.ProviderName</c>, nunca se le muestra al usuario (T-5).
/// </summary>
public record PublicDollarRateReading(decimal Rate, string ProviderName);
