using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "hallazgo del dueño en vivo" + "el dolar nunca falta"): implementacion
/// real de las CINCO APIs publicas de respaldo. Contrato verificado con <c>curl</c> contra las APIs
/// reales el 2026-08-05 (no se asumio la forma de ninguna respuesta):
///
/// <list type="bullet">
///   <item><c>GET https://dolarapi.com/v1/dolares/oficial</c> -&gt; 200 con un objeto
///   <c>{ moneda, casa, nombre, compra, venta, fechaActualizacion }</c>. Ruta invalida -&gt; 404 con
///   cuerpo vacio.</item>
///   <item><c>GET https://monedapi.ar/api/v2/usd/bna</c> -&gt; 200 con un objeto
///   <c>{ currency, name, origin, buy, sell, updatedAt, lastScrapedAt, valueType }</c>. El campo
///   <c>sell</c> es la venta del BNA (equivalente a "venta" en los demas proveedores).</item>
///   <item><c>GET https://criptoya.com/api/bancostodos</c> -&gt; 200 con un objeto donde CADA CLAVE
///   es un banco (<c>{ "bna": { ask, totalAsk, bid, totalBid, time }, "bapro": {...}, ... }</c>). Este
///   servicio SOLO lee la clave <c>"bna"</c> y descarta el resto — el endpoint no tiene una variante
///   "solo BNA", trae todos los bancos en una unica respuesta.</item>
///   <item><c>GET https://api.argentinadatos.com/v1/cotizaciones/dolares/oficial/{yyyy}/{MM}/{dd}</c>
///   -&gt; 200 con un objeto <c>{ casa, compra, venta, fecha }</c> (OJO: sin el array envolvente que
///   trae la variante sin fecha, que devuelve TODO el historico desde 2011 — nunca usar esa variante
///   aca, es una respuesta de cientos de KB). Fecha sin dato (ej. muy futura) -&gt; 404
///   <c>{"error":"Not found"}</c>. Fines de semana SI devuelven fila (la API arrastra el valor del
///   ultimo dia habil con la fecha pedida), asi que este servicio no necesita su propio walk-back.
///   Verificado que la variante por-fecha TAMBIEN sirve el dia de HOY (no hace falta un endpoint
///   separado para eso).</item>
///   <item><c>GET https://api.bluelytics.com.ar/v2/latest</c> -&gt; 200 con
///   <c>{ oficial: { value_avg, value_sell, value_buy }, blue: {...}, ... }</c>. El campo
///   <c>oficial.value_sell</c> es un PROMEDIO de mercado (no el BNA puntual) — se usa solo como
///   ULTIMO respaldo, ver <see cref="IOfficialDollarPublicApiService.GetTodayRateFromBluelyticsAsync"/>.</item>
/// </list>
///
/// <para><b>Euro y real (ampliacion 2026-08-06)</b>: el detalle completo de que URL y que campo usa
/// cada proveedor para estas dos monedas, y CUALES proveedores NO las cubren (criptoya ninguna de
/// las dos, bluelytics solo real), vive en el doc de clase de <see cref="IOfficialDollarPublicApiService"/>
/// — no se repite aca para no tener el mismo detalle desactualizable en dos lugares.</para>
/// </summary>
public class OfficialDollarPublicApiService : IOfficialDollarPublicApiService
{
    private static readonly Uri DolarApiTodayUri = new("https://dolarapi.com/v1/dolares/oficial");
    private static readonly Uri MonedApiBnaUri = new("https://monedapi.ar/api/v2/usd/bna");
    private static readonly Uri CriptoYaBancosUri = new("https://criptoya.com/api/bancostodos");
    private static readonly Uri BluelyticsLatestUri = new("https://api.bluelytics.com.ar/v2/latest");

    // Ampliacion 2026-08-06 (euro/real): dolarapi.com y monedapi.ar exponen la MISMA forma de
    // respuesta para cualquier moneda, solo cambia el codigo en la URL — a diferencia de
    // argentinadatos.com, cuya ruta por-fecha de dolar (con "/oficial") es DISTINTA de la de
    // euro/real (sin "/oficial"), ver el doc de clase de la interfaz para el detalle verificado con curl.
    private static readonly Uri DolarApiEurTodayUri = new("https://dolarapi.com/v1/cotizaciones/eur");
    private static readonly Uri DolarApiBrlTodayUri = new("https://dolarapi.com/v1/cotizaciones/brl");
    private static readonly Uri MonedApiEurUri = new("https://monedapi.ar/api/v2/eur/bna");
    private static readonly Uri MonedApiBrlUri = new("https://monedapi.ar/api/v2/brl/bna");

    private const string DolarApiProviderName = "dolarapi";
    private const string MonedApiProviderName = "monedapi";
    private const string CriptoYaProviderName = "criptoya";
    private const string ArgentinaDatosProviderName = "argentinadatos";
    private const string BluelyticsProviderName = "bluelytics";

    /// <summary>
    /// Timeout corto (T-12): esto corre dentro del job diario/on-demand, nunca en un camino
    /// interactivo, pero igual no puede colgarse esperando una API de terceros — si no contesta
    /// rapido, el job sigue con el siguiente respaldo de la escalera.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OfficialDollarPublicApiService> _logger;

    public OfficialDollarPublicApiService(IHttpClientFactory httpClientFactory, ILogger<OfficialDollarPublicApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<PublicDollarRateReading?> GetTodayRateAsync(CancellationToken cancellationToken) =>
        FetchAsync(DolarApiTodayUri, DolarApiProviderName, ExtractVentaField, cancellationToken);

    public Task<PublicDollarRateReading?> GetTodayRateFromMonedApiAsync(CancellationToken cancellationToken) =>
        FetchAsync(MonedApiBnaUri, MonedApiProviderName, ExtractMonedApiSellField, cancellationToken);

    public Task<PublicDollarRateReading?> GetTodayRateFromCriptoYaAsync(CancellationToken cancellationToken) =>
        FetchAsync(CriptoYaBancosUri, CriptoYaProviderName, ExtractCriptoYaBnaAskField, cancellationToken);

    public Task<PublicDollarRateReading?> GetRateForDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        // Formato explicito con cultura invariante: la URL no puede depender de la configuracion
        // regional del servidor (una cultura con separador de miles distinto en el año, por ejemplo,
        // rompería silenciosamente la ruta).
        var datePath = date.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture);
        var uri = new Uri($"https://api.argentinadatos.com/v1/cotizaciones/dolares/oficial/{datePath}");
        return FetchAsync(uri, ArgentinaDatosProviderName, ExtractVentaField, cancellationToken);
    }

    public Task<PublicDollarRateReading?> GetTodayRateFromBluelyticsAsync(CancellationToken cancellationToken) =>
        FetchAsync(BluelyticsLatestUri, BluelyticsProviderName, ExtractBluelyticsOficialVentaField, cancellationToken);

    // ============================================================
    // EURO (ampliacion 2026-08-06). Sin criptoya: verificado con curl que /api/bancostodos no tiene
    // variante de euro (devuelve {"error":"Invalid pair"}).
    // ============================================================

    public Task<PublicDollarRateReading?> GetTodayRateForEurAsync(CancellationToken cancellationToken) =>
        FetchAsync(DolarApiEurTodayUri, DolarApiProviderName, ExtractVentaField, cancellationToken);

    public Task<PublicDollarRateReading?> GetTodayRateForEurFromMonedApiAsync(CancellationToken cancellationToken) =>
        FetchAsync(MonedApiEurUri, MonedApiProviderName, ExtractMonedApiSellField, cancellationToken);

    public Task<PublicDollarRateReading?> GetEurRateForDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var datePath = date.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture);
        var uri = new Uri($"https://api.argentinadatos.com/v1/cotizaciones/eur/{datePath}");
        return FetchAsync(uri, ArgentinaDatosProviderName, ExtractVentaField, cancellationToken);
    }

    public Task<PublicDollarRateReading?> GetTodayRateForEurFromBluelyticsAsync(CancellationToken cancellationToken) =>
        FetchAsync(BluelyticsLatestUri, BluelyticsProviderName, ExtractBluelyticsOficialEuroVentaField, cancellationToken);

    // ============================================================
    // REAL (ampliacion 2026-08-06). Sin criptoya (mismo motivo que euro) NI bluelytics: verificado
    // con curl que /v2/latest no trae NINGUNA clave de real (ni "oficial_real" ni parecido).
    // ============================================================

    public Task<PublicDollarRateReading?> GetTodayRateForBrlAsync(CancellationToken cancellationToken) =>
        FetchAsync(DolarApiBrlTodayUri, DolarApiProviderName, ExtractVentaField, cancellationToken);

    public Task<PublicDollarRateReading?> GetTodayRateForBrlFromMonedApiAsync(CancellationToken cancellationToken) =>
        FetchAsync(MonedApiBrlUri, MonedApiProviderName, ExtractMonedApiSellField, cancellationToken);

    public Task<PublicDollarRateReading?> GetBrlRateForDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var datePath = date.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture);
        var uri = new Uri($"https://api.argentinadatos.com/v1/cotizaciones/brl/{datePath}");
        return FetchAsync(uri, ArgentinaDatosProviderName, ExtractVentaField, cancellationToken);
    }

    /// <summary>
    /// Pide el JSON y le aplica <paramref name="extractRate"/> (uno distinto por proveedor, cada uno
    /// sabe donde vive "venta" dentro de SU forma de respuesta particular) para sacar el numero.
    /// Cualquier falla (red, timeout, 404, JSON con forma distinta, valor invalido) se loguea como
    /// Warning y devuelve <c>null</c>: nunca tira. Compartir este metodo entre los 5 proveedores es
    /// lo que evita repetir 5 veces el mismo manejo de timeout/reintento/logging — la UNICA parte
    /// que cambia de un proveedor a otro es donde esta el numero dentro del JSON.
    /// </summary>
    private async Task<PublicDollarRateReading?> FetchAsync(
        Uri uri, string providerName, Func<JsonDocument, decimal?> extractRate, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MagnaTravel/1.0");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                // 404 es un caso ESPERADO (fecha sin dato en argentinadatos.com, o el endpoint no
                // responde ese dia): no es un error de sistema, es "sin dato util" — Warning igual
                // para poder ver en los logs si un proveedor empieza a fallar seguido.
                _logger.LogWarning(
                    "OfficialDollarPublicApiService: {Provider} respondio {StatusCode} para {Uri}.",
                    providerName, (int)response.StatusCode, uri);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            using var document = JsonDocument.Parse(body);

            var rate = extractRate(document);
            if (rate is null)
            {
                _logger.LogWarning(
                    "OfficialDollarPublicApiService: {Provider} devolvio un JSON sin el campo de venta esperado.",
                    providerName);
                return null;
            }

            if (rate.Value <= 0m)
            {
                _logger.LogWarning(
                    "OfficialDollarPublicApiService: {Provider} devolvio una cotizacion invalida ({Rate}).",
                    providerName, rate.Value);
                return null;
            }

            return new PublicDollarRateReading(Rate: rate.Value, ProviderName: providerName);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout de la ventana corta, falla de red, JSON invalido (JsonException) — cualquiera
            // de estas es "el respaldo no sirvio ahora", no un motivo para tumbar el job.
            _logger.LogWarning(ex, "OfficialDollarPublicApiService: fallo consultando {Provider} ({Uri}).", providerName, uri);
            return null;
        }
    }

    /// <summary>
    /// dolarapi.com / argentinadatos.com: el numero vive en la raiz del objeto, campo <c>venta</c>.
    /// <c>internal</c> (no <c>private</c>) a proposito: el assembly de tests tiene
    /// <c>InternalsVisibleTo</c>, asi los tests de parseo llaman esto DIRECTO con el JSON real que se
    /// vio por <c>curl</c>, sin tener que mockear <c>HttpClient</c> para probar solo el parseo.
    /// </summary>
    internal static decimal? ExtractVentaField(JsonDocument document) =>
        TryGetDecimalProperty(document.RootElement, "venta");

    /// <summary>monedapi.ar: el numero vive en la raiz del objeto, campo <c>sell</c>.</summary>
    internal static decimal? ExtractMonedApiSellField(JsonDocument document) =>
        TryGetDecimalProperty(document.RootElement, "sell");

    /// <summary>
    /// criptoya.com: la respuesta trae TODOS los bancos en un solo objeto; hay que entrar primero a
    /// la clave <c>"bna"</c> y de ahi sacar <c>ask</c> (lo que el banco pide para VENDER dolares —
    /// equivalente a "venta"/"sell" en los demas proveedores).
    /// </summary>
    internal static decimal? ExtractCriptoYaBnaAskField(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("bna", out var bnaElement))
        {
            return null;
        }
        return TryGetDecimalProperty(bnaElement, "ask");
    }

    /// <summary>
    /// bluelytics.com.ar: hay que entrar primero a la clave <c>"oficial"</c> y de ahi sacar
    /// <c>value_sell</c>. Recordar (ver doc de clase): este numero es un PROMEDIO de mercado, no el
    /// BNA puntual.
    /// </summary>
    internal static decimal? ExtractBluelyticsOficialVentaField(JsonDocument document) =>
        ExtractBluelyticsValueSellField(document, "oficial");

    /// <summary>
    /// bluelytics.com.ar para EURO (ampliacion 2026-08-06): la clave raiz es <c>"oficial_euro"</c> en
    /// vez de <c>"oficial"</c>, pero el resto de la forma es identica (<c>value_sell</c> adentro).
    /// Reusa <see cref="ExtractBluelyticsValueSellField"/> en vez de repetir el mismo
    /// <c>TryGetProperty</c> dos veces — la UNICA diferencia real entre dolar y euro en este
    /// proveedor es el nombre de la clave.
    /// </summary>
    internal static decimal? ExtractBluelyticsOficialEuroVentaField(JsonDocument document) =>
        ExtractBluelyticsValueSellField(document, "oficial_euro");

    private static decimal? ExtractBluelyticsValueSellField(JsonDocument document, string rootPropertyName)
    {
        if (!document.RootElement.TryGetProperty(rootPropertyName, out var element))
        {
            return null;
        }
        return TryGetDecimalProperty(element, "value_sell");
    }

    /// <summary>
    /// Lee una propiedad numerica de un <see cref="JsonElement"/> de forma defensiva: si la
    /// propiedad no existe, o no es un numero (ej. un proveedor cambio la forma de su respuesta y
    /// ahora manda un string), devuelve <c>null</c> en vez de tirar una excepcion de parseo — el
    /// caller (<see cref="FetchAsync"/>) ya sabe tratar un <c>null</c> como "este proveedor no sirvio
    /// esta vez, seguir con el siguiente de la escalera".
    /// </summary>
    private static decimal? TryGetDecimalProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var valueElement))
        {
            return null;
        }
        if (valueElement.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        return valueElement.GetDecimal();
    }
}
