using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): tests focales de
/// <c>GET /api/exchange-rates/suggestion</c>. Controller fino: el foco es el CONTRATO (T-5: nada de
/// internos en la respuesta; 204 sin error cuando no hay dato) y que "hoy" se resuelve con
/// <see cref="ArgentinaTime"/>, no con la hora del servidor.
/// </summary>
public class ExchangeRatesControllerTests
{
    private static ExchangeRatesController BuildController(Mock<IExchangeRateResolver> resolverMock) =>
        new(resolverMock.Object);

    // ============================================================
    // Test 22 (spec §15): 204 cuando no hay dato; el cuerpo no lleva internos.
    // ============================================================

    [Fact]
    public async Task SinSugerencia_Devuelve204_SinCuerpo()
    {
        var resolverMock = new Mock<IExchangeRateResolver>();
        resolverMock
            .Setup(r => r.GetSuggestionAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((ExchangeRateSuggestion?)null);

        var controller = BuildController(resolverMock);

        var result = await controller.GetSuggestion("USD", new DateOnly(2026, 08, 05), CancellationToken.None);

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task ConSugerencia_Devuelve200_ConSoloConceptosDeNegocio()
    {
        var resolverMock = new Mock<IExchangeRateResolver>();
        resolverMock
            .Setup(r => r.GetSuggestionAsync("USD", new DateOnly(2026, 08, 05), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1350.5m,
                RateDate: new DateOnly(2026, 08, 04),
                Source: ExchangeRateSource.AfipOficial,
                ProviderName: "ARCA_WSFEv1",
                ArcaFchCotiz: new DateOnly(2026, 08, 04),
                IsStale: true,
                QuoteId: 42,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));
        resolverMock
            .Setup(r => r.GetInvoicingCeilingAsync("USD", new DateOnly(2026, 08, 05), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1351.5m);

        var controller = BuildController(resolverMock);

        var actionResult = await controller.GetSuggestion("USD", new DateOnly(2026, 08, 05), CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var body = Assert.IsType<ExchangeRateSuggestionResponse>(okResult.Value);

        Assert.Equal(1350.5m, body.TipoCambio);
        Assert.Equal(new DateOnly(2026, 08, 04), body.Fecha);
        Assert.True(body.EsDeOtraFecha);
        // "Ayuda invisible" (spec firmada 2026-08-06, A5.7): el techo del dia viaja YA CALCULADO. La
        // pantalla nunca le suma un peso a nada por su cuenta (regla T-13).
        Assert.Equal(1351.5m, body.TopeDelDia);
        Assert.False(body.LoCompletaElSistema);

        // T-5: la respuesta serializable es EXCLUSIVAMENTE ExchangeRateSuggestionResponse (6
        // propiedades de negocio) — no hay forma de que ProviderName/QuoteId/Source/IsProductionSource
        // se filtren porque ni siquiera existen como propiedades del tipo que se serializa.
        var propertyNames = typeof(ExchangeRateSuggestionResponse).GetProperties();
        Assert.Equal(6, propertyNames.Length);
    }

    /// <summary>
    /// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, tabla A6, decision P1=A): la
    /// leyenda queda en su MINIMO — qué dólar es y de qué día. Texto FIJADO literal (T-6). Dos cosas
    /// que este test protege: que no vuelva la muletilla "Si ponés otro número, lo tomamos a mano." y
    /// que no vuelva la variante "de hoy (…)".
    /// </summary>
    [Theory]
    [InlineData(ExchangeRateSource.AfipOficial, "Dólar oficial del 6 de agosto.")]
    [InlineData(ExchangeRateSource.OficialPorApi, "Dólar Banco Nación del 6 de agosto.")]
    [InlineData(ExchangeRateSource.BNA_Minorista, "Dólar Banco Nación del 6 de agosto.")]
    public async Task LaLeyendaDiceQueDolarEsYDeQueDia_YNadaMas(ExchangeRateSource source, string leyendaEsperada)
    {
        var fecha = new DateOnly(2026, 08, 06);
        var resolverMock = new Mock<IExchangeRateResolver>();
        resolverMock
            .Setup(r => r.GetSuggestionAsync("USD", fecha, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1234.5m,
                RateDate: fecha,
                Source: source,
                ProviderName: "cualquiera",
                ArcaFchCotiz: fecha,
                IsStale: false,
                QuoteId: 7,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));

        var controller = BuildController(resolverMock);

        var actionResult = await controller.GetSuggestion("USD", fecha, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var body = Assert.IsType<ExchangeRateSuggestionResponse>(okResult.Value);
        Assert.Equal(leyendaEsperada, body.Leyenda);
    }

    /// <summary>
    /// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, A3 + Parte C, decision P2=A):
    /// cuando el numero lo completa el motor, la pantalla NO dibuja el casillero — y la respuesta no
    /// lleva ni el numero ni una sola palabra. La leyenda vieja ("Dólar de prueba de ARCA…") MURIO.
    /// </summary>
    [Fact]
    public async Task CuandoLoCompletaElSistema_NoViajaNiElNumeroNiNingunTexto()
    {
        var resolverMock = new Mock<IExchangeRateResolver>();
        resolverMock
            .Setup(r => r.GetSuggestionAsync("USD", new DateOnly(2026, 08, 05), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1152.202m,
                RateDate: new DateOnly(2026, 08, 05),
                Source: ExchangeRateSource.AfipOficial,
                ProviderName: "ARCA_WSFEv1",
                ArcaFchCotiz: new DateOnly(2026, 08, 05),
                IsStale: false,
                QuoteId: 99,
                FetchedAt: DateTime.UtcNow,
                // Este es el unico dato que distingue el caso: el numero no es plata de verdad.
                IsProductionSource: false));

        var controller = BuildController(resolverMock);

        var actionResult = await controller.GetSuggestion("USD", new DateOnly(2026, 08, 05), CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var body = Assert.IsType<ExchangeRateSuggestionResponse>(okResult.Value);

        Assert.True(body.LoCompletaElSistema);
        Assert.Null(body.TipoCambio);
        Assert.Null(body.TopeDelDia);
        Assert.Equal(string.Empty, body.Leyenda);
    }

    [Fact]
    public async Task ConMonedaInvalida_Devuelve400()
    {
        var resolverMock = new Mock<IExchangeRateResolver>();
        var controller = BuildController(resolverMock);

        var actionResult = await controller.GetSuggestion("EUR", new DateOnly(2026, 08, 05), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        resolverMock.Verify(
            r => r.GetSuggestionAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
    }

    // ============================================================
    // Test 8 / §5.3 punto 4, adaptado a lo que SI es determinista sin un reloj inyectable (ver nota
    // en ExchangeRateResolverTests): cuando el front no manda "date", el controller tiene que pedirle
    // al resolver la fecha de ArgentinaTime.GetArgentinaToday(), NUNCA DateTime.UtcNow.Date/Today.
    // Este test compara contra el MISMO helper que exige la regla — si alguien lo cambia por
    // DateTime.UtcNow.Date, este test lo detecta en la franja horaria 21-24hs ART sin necesidad de
    // fijar el reloj.
    // ============================================================

    [Fact]
    public async Task SinFechaEnElQuery_UsaHoyDeArgentinaTime_NoLaDelServidor()
    {
        DateOnly? fechaRecibidaPorElResolver = null;

        var resolverMock = new Mock<IExchangeRateResolver>();
        resolverMock
            .Setup(r => r.GetSuggestionAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, DateOnly, CancellationToken, bool>((_, date, _, _) => fechaRecibidaPorElResolver = date)
            .ReturnsAsync((ExchangeRateSuggestion?)null);

        var controller = BuildController(resolverMock);

        await controller.GetSuggestion("USD", date: null, cancellationToken: CancellationToken.None);

        var hoyArgentina = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());
        Assert.Equal(hoyArgentina, fechaRecibidaPorElResolver);
    }

    // ─── TRABAJO 2: POST /api/exchange-rates/refresh (boton "actualizar" de la tira, 2026-08-05) ──

    /// <summary>
    /// Texto FIJADO literal (T-5/T-6, mismo criterio que <see cref="ConSugerenciaDePractica_LaLeyendaAvisaQueNoEsElDolarReal"/>):
    /// si alguien cambia el mensaje sin querer, este test lo detecta. Nunca 200: 202 porque es
    /// fire-and-forget, el job todavia no corrio cuando el controller ya respondio.
    /// </summary>
    [Fact]
    public async Task Refresh_EncolaLaSincronizacion_YDevuelve202ConMensajeEnCriollo()
    {
        var resolverMock = new Mock<IExchangeRateResolver>();
        resolverMock
            .Setup(r => r.RequestManualSyncAsync("USD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = BuildController(resolverMock);

        var result = await controller.Refresh(CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = accepted.Value;
        Assert.NotNull(body);

        var messageProperty = body!.GetType().GetProperty("message");
        Assert.NotNull(messageProperty);
        Assert.Equal("Buscando el dólar de hoy. En unos segundos se actualiza.", messageProperty!.GetValue(body));
    }

    /// <summary>
    /// Idempotencia (P-21, "el sistema no le hace saber al usuario un detalle tecnico"): aunque el
    /// resolver diga que NO encolo de nuevo (estaba debounced), el controller responde 202 exactamente
    /// IGUAL — para el usuario, el pedido "ya esta en camino" en los dos casos.
    /// </summary>
    [Fact]
    public async Task Refresh_AunqueElResolverDigaQueEstabaDebounced_Devuelve202Igual()
    {
        var resolverMock = new Mock<IExchangeRateResolver>();
        resolverMock
            .Setup(r => r.RequestManualSyncAsync("USD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var controller = BuildController(resolverMock);

        var result = await controller.Refresh(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
    }
}
