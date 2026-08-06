using System.Text.Json;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "el dolar nunca falta"): tests de PARSEO puro para cada uno de los
/// 5 proveedores de <see cref="OfficialDollarPublicApiService"/>. Los fixtures de JSON de abajo son
/// COPIA LITERAL de lo que devolvio cada API real via <c>curl</c> el 2026-08-05 (ver el comentario de
/// clase de <see cref="OfficialDollarPublicApiService"/> para el detalle de cada contrato) — no se
/// inventa ninguna forma de respuesta.
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
}
