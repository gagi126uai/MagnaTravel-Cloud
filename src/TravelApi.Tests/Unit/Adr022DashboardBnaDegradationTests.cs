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
/// FIX 2 (2026-06-12): el dashboard NUNCA debe quedarse bloqueado esperando a Banco Nacion. La cotizacion del
/// dolar es informativa: si el fetch en vivo falla o tarda, el dashboard se degrada al ultimo snapshot
/// persistido y, si no hay, a null — pero siempre responde.
///
/// <para>Estos tests usan un mock de <see cref="IBnaExchangeRateService"/> que SIMULA la falla del fetch en
/// vivo (GetUsdSellerRateAsync lanza) y verifican que GetDashboardAsync igual responde, cayendo a
/// GetPersistedUsdSellerRateAsync (o a null). No verificamos el timeout real de 2s con un fetch HTTP de verdad
/// (eso seria un test lento/de integracion); cubrimos el contrato de degradacion, que es lo que evita el cuelgue.</para>
/// </summary>
public class Adr022DashboardBnaDegradationTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static BnaUsdSellerRateDto PersistedSnapshot() => new(
        Value: 1234.50m,
        EuroValue: 1300m,
        RealValue: 250m,
        PublishedDate: "10/06/2026",
        PublishedTime: "15:00",
        Source: "https://www.bna.com.ar/personas",
        IsStale: true,
        FetchedAt: DateTime.UtcNow.AddHours(-3));

    [Fact]
    public async Task Dashboard_WhenLiveBnaFetchThrows_DegradesToPersistedSnapshot()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        // El fetch en vivo "se cuelga"/falla: lanza. El dashboard NO debe propagar esto.
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("BNA no responde"));
        // El respaldo persistido (lectura local) si esta disponible.
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(PersistedSnapshot());

        var service = new ReportService(context, bna.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        // Respondio (no se colgo ni tiro) y trae el snapshot degradado.
        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(1234.50m, dashboard.BnaUsdSellerRate!.Value);
        Assert.True(dashboard.BnaUsdSellerRate.IsStale);
        bna.Verify(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dashboard_WhenLiveBnaFails_AndNoPersistedSnapshot_DegradesToNull()
    {
        await using var context = CreateContext();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BNA caido"));
        // No hay snapshot persistido todavia.
        bna.Setup(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);

        var service = new ReportService(context, bna.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        // El dashboard responde igual; la cotizacion viene null (el front la tolera).
        Assert.Null(dashboard.BnaUsdSellerRate);
    }

    [Fact]
    public async Task Dashboard_WhenLiveBnaSucceeds_UsesLiveRate_AndDoesNotReadPersisted()
    {
        await using var context = CreateContext();

        var live = PersistedSnapshot() with { Value = 9999m, IsStale = false };

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(live);

        var service = new ReportService(context, bna.Object);

        var dashboard = await service.GetDashboardAsync(CancellationToken.None);

        Assert.NotNull(dashboard.BnaUsdSellerRate);
        Assert.Equal(9999m, dashboard.BnaUsdSellerRate!.Value);
        // Camino feliz: no se toca el respaldo persistido.
        bna.Verify(b => b.GetPersistedUsdSellerRateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
