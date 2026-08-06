using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, decision firmada del dueño): tests focales de la tarjeta 2 del
/// dashboard, "Dólar para facturar (ARCA)" — <see cref="DashboardResponse.DolarParaFacturar"/>. A
/// diferencia de <see cref="Adr022DashboardBnaDegradationTests"/> (tarjeta 1, "solo datos reales"),
/// esta tarjeta pide al resolver EXACTAMENTE lo mismo que la pantalla de facturar (sin el modo
/// "solo datos reales"), porque su proposito es mostrar "lo que la factura va a usar ahora mismo".
/// </summary>
public class Adr011DashboardDolarParaFacturarTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static Mock<IBnaExchangeRateService> NeutralBnaMock()
    {
        // La tarjeta 1 no es el foco de estos tests: se la deja sin dato para que no interfiera.
        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);
        return bna;
    }

    /// <summary>
    /// Camino feliz en producción: AfipOficial productivo -> EsDePrueba en false.
    /// </summary>
    [Fact]
    public async Task ConAfipOficialProductivo_DevuelveElValor_ConEsDePruebaEnFalse()
    {
        await using var context = CreateContext();
        var bna = NeutralBnaMock();

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1496.50m,
                RateDate: new DateOnly(2026, 08, 04),
                Source: ExchangeRateSource.AfipOficial,
                ProviderName: "ARCA_WSFEv1",
                ArcaFchCotiz: new DateOnly(2026, 08, 04),
                IsStale: true,
                QuoteId: 55,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: true));

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);
        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dashboard.DolarParaFacturar);
        Assert.Equal(1496.50m, dashboard.DolarParaFacturar!.Value);
        Assert.Equal(new DateOnly(2026, 08, 04), dashboard.DolarParaFacturar.RateDate);
        Assert.False(dashboard.DolarParaFacturar.EsDePrueba);
    }

    /// <summary>
    /// Homologación: el resolver sirve el AfipOficial de práctica igual que a la factura (mismo
    /// llamado, mismo dato) — pero la tarjeta lo marca EsDePrueba=true para el badge ambar.
    /// </summary>
    [Fact]
    public async Task ConAfipOficialDePractica_DevuelveElValor_ConEsDePruebaEnTrue()
    {
        await using var context = CreateContext();
        var bna = NeutralBnaMock();

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(new ExchangeRateSuggestion(
                Rate: 1152.202m,
                RateDate: new DateOnly(2026, 08, 05),
                Source: ExchangeRateSource.AfipOficial,
                ProviderName: "ARCA_WSFEv1",
                ArcaFchCotiz: new DateOnly(2026, 08, 05),
                IsStale: false,
                QuoteId: 12,
                FetchedAt: DateTime.UtcNow,
                IsProductionSource: false));

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);
        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dashboard.DolarParaFacturar);
        Assert.Equal(1152.202m, dashboard.DolarParaFacturar!.Value);
        Assert.True(dashboard.DolarParaFacturar.EsDePrueba);
    }

    /// <summary>
    /// Sin sugerencia del resolver (job todavia no corrio) -> null, estado vacio honesto en la tarjeta.
    /// </summary>
    [Fact]
    public async Task SinSugerenciaDelResolver_DevuelveNull()
    {
        await using var context = CreateContext();
        var bna = NeutralBnaMock();

        var resolver = new Mock<IExchangeRateResolver>();
        resolver
            .Setup(r => r.GetSuggestionAsync("USD", It.IsAny<DateOnly>(), It.IsAny<CancellationToken>(), false))
            .ReturnsAsync((ExchangeRateSuggestion?)null);

        var service = new ReportService(context, bna.Object, exchangeRateResolver: resolver.Object);
        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Null(dashboard.DolarParaFacturar);
    }

    /// <summary>
    /// Sin resolver inyectado (ctor corto, unit tests preexistentes) -> null, nunca tumba el dashboard.
    /// </summary>
    [Fact]
    public async Task SinResolverInyectado_DevuelveNull_YElRestoDelDashboardSigueRespondiendo()
    {
        await using var context = CreateContext();
        var bna = NeutralBnaMock();

        var service = new ReportService(context, bna.Object);
        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.Null(dashboard.DolarParaFacturar);
    }
}
