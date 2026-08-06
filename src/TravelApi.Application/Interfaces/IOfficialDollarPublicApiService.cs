namespace TravelApi.Application.Interfaces;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "hallazgo del dueño en vivo" + "el dolar nunca falta"; ampliada
/// 2026-08-06 a EUR/BRL): respaldo REAL del dolar oficial (y, desde la ampliacion, tambien euro y
/// real) via VARIAS APIs publicas, para cuando ARCA no sirve un numero util. Motivo concreto: en
/// homologacion, <c>FEParamGetCotizacion</c> devuelve cotizaciones de JUGUETE (verificado
/// 2026-08-05: dio 1152.202 cuando el oficial real de ese dia rondaba 1496) — sin este respaldo, el
/// dashboard mostraba un numero falso o quedaba mudo. ARCA solo cotiza dolar (<c>MonId="DOL"</c>);
/// para euro y real, esta escalera de APIs publicas es la UNICA fuente — no hay equivalente a
/// <c>IAfipService.GetOfficialExchangeRateAsync</c> para esas dos monedas (la factura solo maneja
/// USD hoy, ver <c>CanMisMonExtResolver</c>).
///
/// <para><b>Cada metodo es UN proveedor distinto, contrato verificado con <c>curl</c> real</b> (nunca
/// se asumio la forma de una respuesta). Dolar (2026-08-05):</para>
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
/// <para><b>Euro y real (verificado con <c>curl</c> el 2026-08-06) — la cobertura NO es pareja entre
/// las cinco APIs</b>, esto se investigo antes de escribir una linea de codigo:</para>
/// <list type="bullet">
///   <item><b>dolarapi.com SI cubre las tres</b>: <c>GET /v1/cotizaciones/eur</c> y
///   <c>GET /v1/cotizaciones/brl</c>, mismisimo formato que <c>/v1/dolares/oficial</c> (campo
///   <c>venta</c> en la raiz).</item>
///   <item><b>monedapi.ar SI cubre las tres</b>: <c>GET /api/v2/eur/bna</c> y
///   <c>GET /api/v2/brl/bna</c>, mismo formato que <c>/api/v2/usd/bna</c> (campo <c>sell</c> en la
///   raiz). OJO verificado: la respuesta de BRL trajo un <c>updatedAt</c> de casi un mes atras el
///   dia de la verificacion — este proveedor no siempre tiene el dato de HOY para real, aunque
///   conteste 200. La defensa de coherencia del job (5%, <c>WarnIfRateDivergesFromSameDayAsync</c>)
///   es la unica red para esto; no se le agrego una validacion de frescura propia a este metodo
///   porque ninguno de los otros cuatro proveedores la tiene tampoco (mismo criterio que ya regia
///   para dolar).</item>
///   <item><b>criptoya.com NO cubre euro ni real</b>: <c>/api/bancostodos</c> (y las variantes
///   <c>/api/bancostodos/eur</c>, <c>/api/eur/bancostodos</c> probadas) devuelven
///   <c>{"error":"Invalid pair"}</c> — es un agregador de bancos SOLO para dolar. Este proveedor
///   queda AFUERA de la escalera de EUR/BRL (no hay <c>GetTodayRateForEurFromCriptoYaAsync</c> ni
///   equivalente de real: no existe nada que llamar).</item>
///   <item><b>argentinadatos.com SI cubre las tres, con una ruta DISTINTA a la de dolar</b>: la
///   variante por-fecha de dolar es <c>/v1/cotizaciones/dolares/oficial/{yyyy}/{MM}/{dd}</c> (con el
///   segmento <c>/oficial</c>); euro y real son <c>/v1/cotizaciones/eur/{yyyy}/{MM}/{dd}</c> y
///   <c>/v1/cotizaciones/brl/{yyyy}/{MM}/{dd}</c> (SIN el segmento <c>/oficial</c> — se probo
///   <c>/eur/oficial/{fecha}</c> primero y devolvio 404, la ruta real es mas corta). Fecha de hoy
///   confirmada servida igual que para dolar.</item>
///   <item><b>bluelytics.com.ar SOLO cubre euro, NO real</b>: <c>GET /v2/latest</c> trae la clave
///   <c>oficial_euro.value_sell</c> (mismo patron que <c>oficial.value_sell</c> del dolar) pero NO
///   trae ninguna clave de real en absoluto — no hay <c>oficial_real</c> ni parecido en la
///   respuesta. Real queda con TRES proveedores nada mas (dolarapi, monedapi, argentinadatos); euro
///   con CUATRO (los mismos cuatro de dolar, menos criptoya).</item>
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

    // ============================================================
    // EURO (ampliacion 2026-08-06, "el euro y el real tampoco tienen que faltar"). Escalera de
    // CUATRO proveedores (sin criptoya, ver el detalle de cobertura en el doc de clase).
    // ============================================================

    /// <summary>Cotizacion de HOY para EURO desde dolarapi.com (<c>GET /v1/cotizaciones/eur</c>),
    /// mismo campo <c>venta</c> que la variante de dolar.</summary>
    Task<PublicDollarRateReading?> GetTodayRateForEurAsync(CancellationToken cancellationToken);

    /// <summary>Cotizacion de HOY para EURO desde monedapi.ar (<c>GET /api/v2/eur/bna</c>), campo
    /// <c>sell</c>.</summary>
    Task<PublicDollarRateReading?> GetTodayRateForEurFromMonedApiAsync(CancellationToken cancellationToken);

    /// <summary>Cotizacion de EURO desde argentinadatos.com para <paramref name="date"/>
    /// (<c>GET /v1/cotizaciones/eur/{yyyy}/{MM}/{dd}</c> — SIN el segmento <c>/oficial</c> que si
    /// lleva la ruta de dolar, ver el doc de clase). Sirve tanto HOY como el backfill, igual que la
    /// variante de dolar.</summary>
    Task<PublicDollarRateReading?> GetEurRateForDateAsync(DateOnly date, CancellationToken cancellationToken);

    /// <summary>Cotizacion de HOY para EURO desde bluelytics.com.ar (<c>GET /v2/latest</c>), clave
    /// <c>oficial_euro.value_sell</c>. Ultimo de la escalera de euro, mismo motivo que en dolar
    /// (promedio de mercado, no el BNA puntual).</summary>
    Task<PublicDollarRateReading?> GetTodayRateForEurFromBluelyticsAsync(CancellationToken cancellationToken);

    // ============================================================
    // REAL (ampliacion 2026-08-06). Escalera de TRES proveedores (sin criptoya NI bluelytics — ver
    // el detalle de cobertura en el doc de clase: bluelytics no tiene ninguna clave de real).
    // ============================================================

    /// <summary>Cotizacion de HOY para REAL desde dolarapi.com (<c>GET /v1/cotizaciones/brl</c>),
    /// campo <c>venta</c>.</summary>
    Task<PublicDollarRateReading?> GetTodayRateForBrlAsync(CancellationToken cancellationToken);

    /// <summary>Cotizacion de HOY para REAL desde monedapi.ar (<c>GET /api/v2/brl/bna</c>), campo
    /// <c>sell</c>. OJO (verificado con curl): este proveedor puede contestar 200 con un dato de
    /// semanas atras para real — no valida frescura por si mismo, igual que el resto de la escalera.</summary>
    Task<PublicDollarRateReading?> GetTodayRateForBrlFromMonedApiAsync(CancellationToken cancellationToken);

    /// <summary>Cotizacion de REAL desde argentinadatos.com para <paramref name="date"/>
    /// (<c>GET /v1/cotizaciones/brl/{yyyy}/{MM}/{dd}</c>, sin segmento <c>/oficial</c>). Unico
    /// proveedor de real con historial: es el que cubre el backfill.</summary>
    Task<PublicDollarRateReading?> GetBrlRateForDateAsync(DateOnly date, CancellationToken cancellationToken);
}

/// <summary>
/// Lectura minima de una de las cinco APIs publicas. <see cref="ProviderName"/> identifica CUAL
/// respondio ("dolarapi" / "monedapi" / "criptoya" / "argentinadatos" / "bluelytics") — es lo que
/// termina en <c>ExchangeRateQuote.ProviderName</c>, nunca se le muestra al usuario (T-5).
/// </summary>
public record PublicDollarRateReading(decimal Rate, string ProviderName);
