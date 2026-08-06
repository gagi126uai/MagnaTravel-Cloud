using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "hallazgo del dueño en vivo"): implementacion real de las dos APIs
/// publicas de respaldo. Contrato verificado con <c>curl</c> contra las APIs reales el 2026-08-05
/// (no se asumio la forma de la respuesta):
///
/// <list type="bullet">
///   <item><c>GET https://dolarapi.com/v1/dolares/oficial</c> -&gt; 200 con un objeto
///   <c>{ moneda, casa, nombre, compra, venta, fechaActualizacion }</c>. Ruta invalida -&gt; 404 con
///   cuerpo vacio.</item>
///   <item><c>GET https://api.argentinadatos.com/v1/cotizaciones/dolares/oficial/{yyyy}/{MM}/{dd}</c>
///   -&gt; 200 con un objeto <c>{ casa, compra, venta, fecha }</c> (OJO: sin el array envolvente que
///   trae la variante sin fecha, que devuelve TODO el historico desde 2011 — nunca usar esa variante
///   aca, es una respuesta de cientos de KB). Fecha sin dato (ej. muy futura) -&gt; 404
///   <c>{"error":"Not found"}</c>. Fines de semana SI devuelven fila (la API arrastra el valor del
///   ultimo dia habil con la fecha pedida), asi que este servicio no necesita su propio walk-back.</item>
/// </list>
/// </summary>
public class OfficialDollarPublicApiService : IOfficialDollarPublicApiService
{
    private static readonly Uri DolarApiTodayUri = new("https://dolarapi.com/v1/dolares/oficial");
    private const string DolarApiProviderName = "dolarapi";
    private const string ArgentinaDatosProviderName = "argentinadatos";

    /// <summary>
    /// Timeout corto (T-12): esto corre dentro del job diario, nunca en un camino interactivo, pero
    /// igual no puede colgarse esperando una API de terceros — si no contesta rapido, el job sigue
    /// con el siguiente respaldo de la escalera (scraper BNA para "hoy", nada para el backfill).
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OfficialDollarPublicApiService> _logger;

    public OfficialDollarPublicApiService(IHttpClientFactory httpClientFactory, ILogger<OfficialDollarPublicApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<PublicDollarRateReading?> GetTodayRateAsync(CancellationToken cancellationToken)
    {
        return await FetchAsync(DolarApiTodayUri, DolarApiProviderName, cancellationToken);
    }

    public async Task<PublicDollarRateReading?> GetRateForDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        // Formato explicito con cultura invariante: la URL no puede depender de la configuracion
        // regional del servidor (una cultura con separador de miles distinto en el año, por ejemplo,
        // rompería silenciosamente la ruta).
        var datePath = date.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture);
        var uri = new Uri($"https://api.argentinadatos.com/v1/cotizaciones/dolares/oficial/{datePath}");
        return await FetchAsync(uri, ArgentinaDatosProviderName, cancellationToken);
    }

    /// <summary>
    /// Pide el JSON, saca el campo <c>venta</c> (lo que el cliente paga — mismo criterio que ARCA
    /// <c>MonCotiz</c> y el BNA vendedor) y lo valida. Cualquier falla (red, timeout, 404, JSON con
    /// forma distinta, valor invalido) se loguea como Warning y devuelve <c>null</c>: nunca tira.
    /// </summary>
    private async Task<PublicDollarRateReading?> FetchAsync(Uri uri, string providerName, CancellationToken cancellationToken)
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

            if (!document.RootElement.TryGetProperty("venta", out var ventaElement))
            {
                _logger.LogWarning(
                    "OfficialDollarPublicApiService: {Provider} devolvio un JSON sin el campo 'venta' esperado.",
                    providerName);
                return null;
            }

            var rate = ventaElement.GetDecimal();
            if (rate <= 0m)
            {
                _logger.LogWarning(
                    "OfficialDollarPublicApiService: {Provider} devolvio una cotizacion invalida ({Rate}).",
                    providerName, rate);
                return null;
            }

            return new PublicDollarRateReading(Rate: rate, ProviderName: providerName);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout de la ventana corta, falla de red, JSON invalido (JsonException) — cualquiera
            // de estas es "el respaldo no sirvio ahora", no un motivo para tumbar el job.
            _logger.LogWarning(ex, "OfficialDollarPublicApiService: fallo consultando {Provider} ({Uri}).", providerName, uri);
            return null;
        }
    }
}
