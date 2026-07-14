using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 Fix B (2026-07-13): sugerencia del dolar oficial BNA para una FECHA pasada
/// (<see cref="BnaExchangeRateService.GetPersistedUsdSellerRateForDateAsync"/>), que pre-escribe el TC del modal
/// de "corregir monto y moneda".
///
/// <para><b>Contexto del modelo (no obvio)</b>: <c>BnaExchangeRateSnapshots</c> es un SINGLETON — guarda UNA sola
/// fila con la ULTIMA cotizacion, no una serie historica. Por eso la consulta solo puede ofrecer ese unico
/// snapshot, y solo si su fecha cae en una ventana corta (&lt;= la pedida, hasta 5 dias antes, para cubrir
/// findes/feriados). Estos tests fijan ese comportamiento honesto: nunca inventa un numero.</para>
/// </summary>
public class BnaExchangeRateServiceForDateTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BnaExchangeRateService NewService(AppDbContext ctx) =>
        new(Mock.Of<System.Net.Http.IHttpClientFactory>(),
            ctx,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<BnaExchangeRateService>.Instance);

    /// <summary>Siembra el UNICO snapshot (Id = SingletonId) con una fecha de publicacion y una cotizacion.</summary>
    private static async Task SeedSnapshotAsync(AppDbContext ctx, string publishedDate, decimal usdSeller)
    {
        ctx.BnaExchangeRateSnapshots.Add(new BnaExchangeRateSnapshot
        {
            Id = BnaExchangeRateSnapshot.SingletonId,
            UsdSeller = usdSeller,
            EuroSeller = 0m,
            RealSeller = 0m,
            PublishedDate = publishedDate,
            PublishedTime = "15:00",
            Source = "https://www.bna.com.ar/personas",
            FetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task ForExactDate_ReturnsRate_WithThatDate()
    {
        await using var ctx = NewContext();
        await SeedSnapshotAsync(ctx, publishedDate: "04/07/2026", usdSeller: 1234.50m);
        var service = NewService(ctx);

        var result = await service.GetPersistedUsdSellerRateForDateAsync(
            new DateOnly(2026, 7, 4), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1234.50m, result!.Rate);
        Assert.Equal(new DateOnly(2026, 7, 4), result.RateDate);
    }

    [Fact]
    public async Task ForWeekendDate_WhenSnapshotIsPrecedingFriday_ReturnsFridayRate_WithinWindow()
    {
        // Se pide el domingo 05/07; el ultimo BNA es del viernes 03/07 (2 dias antes, dentro de la ventana de 5).
        // Devuelve la cotizacion del viernes y su fecha REAL (para que el front muestre "del 03/07").
        await using var ctx = NewContext();
        await SeedSnapshotAsync(ctx, publishedDate: "03/07/2026", usdSeller: 1000m);
        var service = NewService(ctx);

        var result = await service.GetPersistedUsdSellerRateForDateAsync(
            new DateOnly(2026, 7, 5), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1000m, result!.Rate);
        Assert.Equal(new DateOnly(2026, 7, 3), result.RateDate);
    }

    [Fact]
    public async Task WhenNoSnapshotPersisted_ReturnsNull()
    {
        await using var ctx = NewContext();
        var service = NewService(ctx);

        var result = await service.GetPersistedUsdSellerRateForDateAsync(
            new DateOnly(2026, 7, 5), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task WhenSnapshotIsOlderThanWindow_ReturnsNull()
    {
        // Snapshot del 01/07; se pide el 10/07 -> 9 dias de gap, fuera de la ventana de 5. Sin dato.
        await using var ctx = NewContext();
        await SeedSnapshotAsync(ctx, publishedDate: "01/07/2026", usdSeller: 1000m);
        var service = NewService(ctx);

        var result = await service.GetPersistedUsdSellerRateForDateAsync(
            new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task WhenSnapshotIsNewerThanRequestedDate_ReturnsNull()
    {
        // Se pide una fecha VIEJA (04/06) pero el unico snapshot es posterior (04/07): no es un dato que
        // corresponda a la fecha pedida. Sin dato (el modal carga a mano).
        await using var ctx = NewContext();
        await SeedSnapshotAsync(ctx, publishedDate: "04/07/2026", usdSeller: 1234.50m);
        var service = NewService(ctx);

        var result = await service.GetPersistedUsdSellerRateForDateAsync(
            new DateOnly(2026, 6, 4), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ForFarFutureDate_ReturnsNull()
    {
        // Fecha futura lejana (+30 dias) con snapshot de hoy: el snapshot es <= la pedida pero el gap supera la
        // ventana -> sin dato. (Una fecha futura no tiene un TC oficial ya publicado.)
        await using var ctx = NewContext();
        await SeedSnapshotAsync(ctx, publishedDate: "01/07/2026", usdSeller: 1000m);
        var service = NewService(ctx);

        var result = await service.GetPersistedUsdSellerRateForDateAsync(
            new DateOnly(2026, 8, 1), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task WhenPublishedDateUnparseable_ReturnsNull()
    {
        await using var ctx = NewContext();
        await SeedSnapshotAsync(ctx, publishedDate: "no-es-una-fecha", usdSeller: 1000m);
        var service = NewService(ctx);

        var result = await service.GetPersistedUsdSellerRateForDateAsync(
            new DateOnly(2026, 7, 5), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task WhenRateIsNotReliable_ReturnsNull()
    {
        // Cotizacion 0 (dato corrupto): no se sugiere.
        await using var ctx = NewContext();
        await SeedSnapshotAsync(ctx, publishedDate: "05/07/2026", usdSeller: 0m);
        var service = NewService(ctx);

        var result = await service.GetPersistedUsdSellerRateForDateAsync(
            new DateOnly(2026, 7, 5), CancellationToken.None);

        Assert.Null(result);
    }
}
