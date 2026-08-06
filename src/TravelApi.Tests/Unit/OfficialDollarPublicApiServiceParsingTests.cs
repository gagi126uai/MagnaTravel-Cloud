using System.Text.Json;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "el dolar nunca falta"; ampliada 2026-08-06 a EUR/BRL): tests de
/// PARSEO puro para cada uno de los proveedores de <see cref="OfficialDollarPublicApiService"/>. Los
/// fixtures de JSON de abajo son COPIA LITERAL de lo que devolvio cada API real via <c>curl</c> el
/// 2026-08-05 (dolar) y el 2026-08-06 (euro/real) — ver el comentario de clase de
/// <see cref="IOfficialDollarPublicApiService"/> para el detalle de cada contrato — no se inventa
/// ninguna forma de respuesta.
///
/// <para>Los metodos <c>internal static</c> de extraccion se llaman DIRECTO (el assembly de
/// Infrastructure tiene <c>InternalsVisibleTo("TravelApi.Tests")</c>): no hace falta mockear
/// <c>HttpClient</c> para probar solo la parte de "donde vive el numero adentro del JSON".</para>
/// </summary>
public class OfficialDollarPublicApiServiceParsingTests
{
    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    // ============================================================
    // dolarapi.com (tambien usado por argentinadatos.com, mismo campo "venta").
    // ============================================================

    [Fact]
    public void ExtractVentaField_ConRespuestaRealDeDolarApi_SacaElCampoVenta()
    {
        const string json = """
            {"moneda":"USD","casa":"oficial","nombre":"Oficial","compra":1470,"venta":1520,"fechaActualizacion":"2026-08-05T12:00:00.000Z"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractVentaField(Parse(json));

        Assert.Equal(1520m, rate);
    }

    [Fact]
    public void ExtractVentaField_ConRespuestaRealDeArgentinaDatos_SacaElCampoVenta()
    {
        const string json = """
            {"casa":"oficial","compra":1470,"venta":1520,"fecha":"2026-08-05"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractVentaField(Parse(json));

        Assert.Equal(1520m, rate);
    }

    [Fact]
    public void ExtractVentaField_SinElCampoVenta_DevuelveNull()
    {
        const string json = """{"casa":"oficial","compra":1470}""";

        var rate = OfficialDollarPublicApiService.ExtractVentaField(Parse(json));

        Assert.Null(rate);
    }

    // ============================================================
    // monedapi.ar — GET /api/v2/usd/bna, campo "sell".
    // ============================================================

    [Fact]
    public void ExtractMonedApiSellField_ConRespuestaRealDeMonedApi_SacaElCampoSell()
    {
        const string json = """
            {"currency":"USD","name":"Dólar Banco Nación","origin":"BNA","buy":1470,"sell":1520,"updatedAt":"2026-08-05T12:02:01.501-03:00","lastScrapedAt":"2026-08-05T12:02:01.501-03:00","valueType":"money"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractMonedApiSellField(Parse(json));

        Assert.Equal(1520m, rate);
    }

    [Fact]
    public void ExtractMonedApiSellField_SinElCampoSell_DevuelveNull()
    {
        const string json = """{"currency":"USD","origin":"BNA","buy":1470}""";

        var rate = OfficialDollarPublicApiService.ExtractMonedApiSellField(Parse(json));

        Assert.Null(rate);
    }

    // ============================================================
    // criptoya.com — GET /api/bancostodos, hay que entrar a la clave "bna" y sacar "ask".
    // ============================================================

    [Fact]
    public void ExtractCriptoYaBnaAskField_ConRespuestaRealDeCriptoYa_EntraALaClaveBnaYSacaAsk()
    {
        // Fixture recortado: la respuesta real trae ~20 bancos mas, el test solo necesita
        // demostrar que el parseo IGNORA todo lo que no sea la clave "bna".
        const string json = """
            {"andina":{"ask":1525,"bid":1475,"time":1785978114},"bna":{"ask":1520,"totalAsk":1520,"bid":1470,"totalBid":1470,"time":1785978117},"bapro":{"ask":1425,"bid":1375,"time":1772653219}}
            """;

        var rate = OfficialDollarPublicApiService.ExtractCriptoYaBnaAskField(Parse(json));

        Assert.Equal(1520m, rate);
    }

    [Fact]
    public void ExtractCriptoYaBnaAskField_SinLaClaveBna_DevuelveNull()
    {
        const string json = """{"andina":{"ask":1525,"bid":1475}}""";

        var rate = OfficialDollarPublicApiService.ExtractCriptoYaBnaAskField(Parse(json));

        Assert.Null(rate);
    }

    // ============================================================
    // bluelytics.com.ar — GET /v2/latest, hay que entrar a "oficial" y sacar "value_sell".
    // ============================================================

    [Fact]
    public void ExtractBluelyticsOficialVentaField_ConRespuestaRealDeBluelytics_EntraAOficialYSacaValueSell()
    {
        const string json = """
            {"oficial":{"value_avg":1494.50,"value_sell":1520.00,"value_buy":1469.00},"blue":{"value_avg":1523.50,"value_sell":1540.00,"value_buy":1507.00},"last_update":"2026-08-05T19:45:53.751063-03:00"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractBluelyticsOficialVentaField(Parse(json));

        Assert.Equal(1520.00m, rate);
    }

    [Fact]
    public void ExtractBluelyticsOficialVentaField_SinLaClaveOficial_DevuelveNull()
    {
        const string json = """{"blue":{"value_sell":1540.00}}""";

        var rate = OfficialDollarPublicApiService.ExtractBluelyticsOficialVentaField(Parse(json));

        Assert.Null(rate);
    }

    // ============================================================
    // Defensa comun a los 5: un valor que no es numero (ej. un proveedor manda un string por error)
    // no tira una excepcion de parseo, devuelve null como cualquier otro "sin dato util".
    // ============================================================

    [Fact]
    public void ExtractVentaField_ConVentaComoTexto_DevuelveNullSinTirar()
    {
        const string json = """{"casa":"oficial","venta":"no-disponible"}""";

        var rate = OfficialDollarPublicApiService.ExtractVentaField(Parse(json));

        Assert.Null(rate);
    }

    // ============================================================
    // Ampliacion 2026-08-06 ("el euro y el real tampoco tienen que faltar"): fixtures COPIA LITERAL
    // de lo que devolvio cada API real via curl el 2026-08-06 (ver el doc de clase de
    // IOfficialDollarPublicApiService para el detalle completo de cobertura por moneda). Los
    // extractores de dolarapi/monedapi/argentinadatos son los MISMOS que ya usa USD (misma forma de
    // respuesta, solo cambia el codigo de moneda en la URL) — estos tests fijan que efectivamente
    // parsean EUR/BRL igual de bien. bluelytics.euro tiene un extractor nuevo (clave "oficial_euro"
    // en vez de "oficial").
    // ============================================================

    [Fact]
    public void ExtractVentaField_ConRespuestaRealDeDolarApiParaEuro_SacaElCampoVenta()
    {
        const string json = """
            {"moneda":"EUR","casa":"oficial","nombre":"Euro","compra":1717.4675,"venta":1731.6002,"fechaActualizacion":"2026-08-05T16:57:00.000Z"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractVentaField(Parse(json));

        Assert.Equal(1731.6002m, rate);
    }

    [Fact]
    public void ExtractVentaField_ConRespuestaRealDeDolarApiParaReal_SacaElCampoVenta()
    {
        const string json = """
            {"moneda":"BRL","casa":"oficial","nombre":"Real Brasileño","compra":291.0318,"venta":291.1998,"fechaActualizacion":"2026-08-05T17:30:00.000Z"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractVentaField(Parse(json));

        Assert.Equal(291.1998m, rate);
    }

    [Fact]
    public void ExtractMonedApiSellField_ConRespuestaRealDeMonedApiParaEuro_SacaElCampoSell()
    {
        const string json = """
            {"currency":"EUR","name":"Euro Banco Nación","origin":"BNA","buy":1668.6,"sell":1764.68,"updatedAt":"2026-08-05T15:56:01.484-03:00","lastScrapedAt":"2026-08-05T15:56:01.484-03:00","valueType":"money"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractMonedApiSellField(Parse(json));

        Assert.Equal(1764.68m, rate);
    }

    /// <summary>
    /// Fixture REAL verificado con curl el 2026-08-06: monedapi.ar contesto 200 para real con un
    /// <c>updatedAt</c> de casi un MES atras (documentado en el doc de clase de la interfaz). El
    /// extractor de todos modos saca el numero correctamente — la validacion de "es de hoy o no" NO
    /// es responsabilidad de este metodo (ningun otro extractor de la escalera la hace tampoco), la
    /// hace la defensa de coherencia del job (5%, WarnIfRateDivergesFromSameDayAsync).
    /// </summary>
    [Fact]
    public void ExtractMonedApiSellField_ConRespuestaRealDeMonedApiParaReal_SacaElCampoSellAunqueSeaViejo()
    {
        const string json = """
            {"currency":"BRL","name":"Real Banco Nación","origin":"BNA","buy":285,"sell":300,"updatedAt":"2026-07-06T15:04:02.325-03:00","lastScrapedAt":"2026-07-06T15:04:02.325-03:00","valueType":"money"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractMonedApiSellField(Parse(json));

        Assert.Equal(300m, rate);
    }

    [Fact]
    public void ExtractVentaField_ConRespuestaRealDeArgentinaDatosParaEuroPorFecha_SacaElCampoVenta()
    {
        // Ruta real verificada: /v1/cotizaciones/eur/{yyyy}/{MM}/{dd} (SIN el segmento "/oficial" que
        // si lleva la ruta de dolar) -> misma forma de respuesta que la variante de dolar.
        const string json = """
            {"moneda":"EUR","casa":"oficial","compra":1717.4675,"venta":1731.6002,"fecha":"2026-08-05"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractVentaField(Parse(json));

        Assert.Equal(1731.6002m, rate);
    }

    [Fact]
    public void ExtractVentaField_ConRespuestaRealDeArgentinaDatosParaRealPorFecha_SacaElCampoVenta()
    {
        const string json = """
            {"moneda":"BRL","casa":"oficial","compra":291.0318,"venta":291.1998,"fecha":"2026-08-05"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractVentaField(Parse(json));

        Assert.Equal(291.1998m, rate);
    }

    // ============================================================
    // bluelytics.com.ar para EURO — clave raiz "oficial_euro" (verificado con curl que NO hay
    // ninguna clave de real en esta respuesta: bluelytics solo cubre dolar+euro).
    // ============================================================

    [Fact]
    public void ExtractBluelyticsOficialEuroVentaField_ConRespuestaRealDeBluelytics_EntraAOficialEuroYSacaValueSell()
    {
        const string json = """
            {"oficial":{"value_avg":1494.50,"value_sell":1520.00,"value_buy":1469.00},"blue":{"value_avg":1523.50,"value_sell":1540.00,"value_buy":1507.00},"oficial_euro":{"value_avg":1624.00,"value_sell":1652.00,"value_buy":1596.00},"blue_euro":{"value_avg":1656.00,"value_sell":1674.00,"value_buy":1638.00},"last_update":"2026-08-05T19:45:53.751063-03:00"}
            """;

        var rate = OfficialDollarPublicApiService.ExtractBluelyticsOficialEuroVentaField(Parse(json));

        Assert.Equal(1652.00m, rate);
    }

    [Fact]
    public void ExtractBluelyticsOficialEuroVentaField_SinLaClaveOficialEuro_DevuelveNull()
    {
        // Fixture SIN "oficial_euro": es exactamente lo que bluelytics devolveria si algun dia dejara
        // de publicar euro (o el escenario de real, que HOY no publica en absoluto).
        const string json = """{"oficial":{"value_sell":1520.00},"blue":{"value_sell":1540.00}}""";

        var rate = OfficialDollarPublicApiService.ExtractBluelyticsOficialEuroVentaField(Parse(json));

        Assert.Null(rate);
    }
}
