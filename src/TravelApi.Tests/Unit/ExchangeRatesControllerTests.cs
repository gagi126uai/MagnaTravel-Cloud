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

        var controller = BuildController(resolverMock);

        var actionResult = await controller.GetSuggestion("USD", new DateOnly(2026, 08, 05), CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var body = Assert.IsType<ExchangeRateSuggestionResponse>(okResult.Value);

        Assert.Equal(1350.5m, body.TipoCambio);
        Assert.Equal(new DateOnly(2026, 08, 04), body.Fecha);
        Assert.True(body.EsDeOtraFecha);
        Assert.False(string.IsNullOrWhiteSpace(body.Leyenda));

        // T-5: la respuesta serializable es EXCLUSIVAMENTE ExchangeRateSuggestionResponse (4
        // propiedades de negocio) — no hay forma de que ProviderName/QuoteId/Source/IsProductionSource
        // se filtren porque ni siquiera existen como propiedades del tipo que se serializa.
        var propertyNames = typeof(ExchangeRateSuggestionResponse).GetProperties();
        Assert.Equal(4, propertyNames.Length);
    }

    /// <summary>
    /// ADR-011 (enmienda 2026-08-05, hallazgo normativo "validacion ARCA 10240"): un AfipOficial de
    /// homologacion (IsProductionSource=false) SIGUE sirviendose como sugerencia — pero la leyenda
    /// tiene que avisar con todas las letras que es un numero de práctica, para que nadie lo confunda
    /// con el dolar real. Texto FIJADO literal (T-6): si alguien lo cambia, este test lo detecta.
    /// </summary>
    [Fact]
    public async Task ConSugerenciaDePractica_LaLeyendaAvisaQueNoEsElDolarReal()
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
                IsProductionSource: false));

        var controller = BuildController(resolverMock);

        var actionResult = await controller.GetSuggestion("USD", new DateOnly(2026, 08, 05), CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var body = Assert.IsType<ExchangeRateSuggestionResponse>(okResult.Value);

        Assert.Equal(1152.202m, body.TipoCambio);
        Assert.Equal(
            "Dólar de prueba de ARCA (el sistema factura en modo práctica): sirve para facturas de prueba, NO es el dólar real.",
            body.Leyenda);
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
}
