using System;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): tests focales de las dos piezas nuevas de
/// <c>AfipService</c> que consumen <c>ExchangeRateSyncJob</c>:
///
/// <list type="bullet">
///   <item><see cref="AfipService.ParseCotizacionResponse"/> — parseo de la respuesta cruda de
///   <c>FEParamGetCotizacion</c>, con los guards de §7.5 (Errors, parseable, coherente).</item>
///   <item><see cref="AfipService.IsAuthTicketValid"/> — el guard del ticket WSAA que respeta
///   <c>IsProduction</c> (a diferencia del bug latente de <c>GetStatus</c>, V5 del diseño).</item>
/// </list>
///
/// Son tests UNITARIOS PUROS: no arman <c>AfipService</c> completo, no tocan HTTP ni BD.
/// </summary>
public class Adr011AfipServiceExchangeRateTests
{
    private const string RequestedMonId = "DOL";
    private const string RequestedFchCotiz = "20260805";

    private static string BuildSuccessResponseXml(string monCotizText, string monId = "DOL", string fchCotiz = "20260805") => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <FEParamGetCotizacionResponse xmlns=""http://ar.gov.afip.dif.FEV1/"">
      <FEParamGetCotizacionResult>
        <ResultGet>
          <MonId>{monId}</MonId>
          <MonCotiz>{monCotizText}</MonCotiz>
          <FchCotiz>{fchCotiz}</FchCotiz>
        </ResultGet>
      </FEParamGetCotizacionResult>
    </FEParamGetCotizacionResponse>
  </soap:Body>
</soap:Envelope>";

    private static string BuildErrorResponseXml(int code, string msg) => $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <FEParamGetCotizacionResponse xmlns=""http://ar.gov.afip.dif.FEV1/"">
      <FEParamGetCotizacionResult>
        <Errors>
          <Err>
            <Code>{code}</Code>
            <Msg>{msg}</Msg>
          </Err>
        </Errors>
      </FEParamGetCotizacionResult>
    </FEParamGetCotizacionResponse>
  </soap:Body>
</soap:Envelope>";

    // ============================================================
    // Test 14 (spec §15): parseo InvariantCulture. "1234.56" -> 1234.56, NUNCA 123456.
    // ============================================================

    [Fact]
    public void ParseCotizacionResponse_ParseaConInvariantCulture_NuncaMultiplicaPor100()
    {
        var xml = BuildSuccessResponseXml("1234.56");

        var result = AfipService.ParseCotizacionResponse(xml, RequestedMonId, RequestedFchCotiz, NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Equal(1234.56m, result!.MonCotiz);
    }

    [Fact]
    public void ParseCotizacionResponse_ConRespuestaValida_DevuelveMonIdYFchCotizDeLaRespuesta()
    {
        var xml = BuildSuccessResponseXml("1350.500000", monId: "DOL", fchCotiz: "20260804");

        var result = AfipService.ParseCotizacionResponse(xml, RequestedMonId, RequestedFchCotiz, NullLogger.Instance);

        Assert.NotNull(result);
        Assert.Equal("DOL", result!.MonId);
        Assert.Equal(1350.5m, result.MonCotiz);
        // FchCotiz devuelto es el que CONTESTO ARCA (20260804), no el que se pidio (20260805) — un
        // domingo ARCA puede contestar con el ultimo dia habil.
        Assert.Equal(new DateOnly(2026, 08, 04), result.FchCotiz);
    }

    // ============================================================
    // Test 10 (spec §15, a nivel parseo): Errors no vacio -> null, sin excepcion.
    // ============================================================

    [Fact]
    public void ParseCotizacionResponse_ConErrorsNoVacio_DevuelveNull_SinTirar()
    {
        var xml = BuildErrorResponseXml(602, "Moneda no encontrada");

        var result = AfipService.ParseCotizacionResponse(xml, RequestedMonId, RequestedFchCotiz, NullLogger.Instance);

        Assert.Null(result);
    }

    // ============================================================
    // Test 13 (spec §15): Rate invalido (0, 1, negativo) -> no se persiste (el parseo ya lo descarta).
    // ============================================================

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-5.5")]
    public void ParseCotizacionResponse_ConCotizacionInvalida_DevuelveNull(string monCotizInvalido)
    {
        var xml = BuildSuccessResponseXml(monCotizInvalido);

        var result = AfipService.ParseCotizacionResponse(xml, RequestedMonId, RequestedFchCotiz, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ParseCotizacionResponse_ConXmlInvalido_DevuelveNull_SinTirar()
    {
        var result = AfipService.ParseCotizacionResponse(
            "esto no es xml", RequestedMonId, RequestedFchCotiz, NullLogger.Instance);

        Assert.Null(result);
    }

    [Fact]
    public void ParseCotizacionResponse_SinResultGetNiErrors_DevuelveNull()
    {
        // ARCA respondio 200 pero sin FEParamGetCotizacionResult (caso raro, defensivo).
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/""><soap:Body></soap:Body></soap:Envelope>";

        var result = AfipService.ParseCotizacionResponse(xml, RequestedMonId, RequestedFchCotiz, NullLogger.Instance);

        Assert.Null(result);
    }

    // ============================================================
    // Test 15 (spec §15): con IsProduction=true y ProdTokenExpiration vencida pero
    // TokenExpiration vigente, IsAuthTicketValid devuelve FALSE (la trampa del bug V5:
    // GetStatus mira el campo equivocado, este helper NO tiene que repetir ese error).
    // ============================================================

    [Fact]
    public void IsAuthTicketValid_EnProduccion_MiraProdTokenExpiration_NoTokenExpiration()
    {
        var settings = new AfipSettings
        {
            IsProduction = true,
            ProdTokenExpiration = DateTime.UtcNow.AddMinutes(-10), // vencido
            TokenExpiration = DateTime.UtcNow.AddHours(10),        // vigente, pero es el de HOMOLOGACION
        };

        var isValid = AfipService.IsAuthTicketValid(settings);

        Assert.False(isValid);
    }

    [Fact]
    public void IsAuthTicketValid_EnHomologacion_MiraTokenExpiration()
    {
        var settings = new AfipSettings
        {
            IsProduction = false,
            TokenExpiration = DateTime.UtcNow.AddHours(10),
            ProdTokenExpiration = null,
        };

        var isValid = AfipService.IsAuthTicketValid(settings);

        Assert.True(isValid);
    }

    [Fact]
    public void IsAuthTicketValid_EnProduccion_ConProdTokenExpirationVigente_DevuelveTrue()
    {
        var settings = new AfipSettings
        {
            IsProduction = true,
            ProdTokenExpiration = DateTime.UtcNow.AddHours(10),
            TokenExpiration = null,
        };

        var isValid = AfipService.IsAuthTicketValid(settings);

        Assert.True(isValid);
    }

    [Fact]
    public void IsAuthTicketValid_SinExpirationCargada_DevuelveFalse()
    {
        var settings = new AfipSettings { IsProduction = true, ProdTokenExpiration = null };

        Assert.False(AfipService.IsAuthTicketValid(settings));
    }
}
