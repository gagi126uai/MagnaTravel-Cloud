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
/// ADR-017 F1.3 (§2.3.b.7): la conversion de presupuesto a reserva upsertea la "ultima venta" por
/// (producto, operador) POST-EXITO best-effort, SOLO con el flag <c>EnableCatalogFindOrCreate</c> ON.
///
/// <para>Asimetria deliberada con el alta transaccional: la conversion ya esta commiteada cuando corre el
/// upsert, asi que si el upsert falla NO se revierte la conversion (la tabla es estadistica de sugerencia,
/// la reconciliacion R7 detecta faltantes). Aca se cubre: dispara con flag ON, NO dispara con flag OFF,
/// skip de supplier 0, y skip de tipos que caen al servicio generico (Asistencia).</para>
/// </summary>
public class QuoteServiceConvertCatalogTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static QuoteService CreateService(AppDbContext context, bool flagOn)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = flagOn });

        return new QuoteService(
            context,
            Mock.Of<IEntityReferenceResolver>(),
            settings.Object);
    }

    // Crea un presupuesto con UN item del tipo pedido, opcionalmente ligado a una tarifa. Devuelve el id
    // interno del quote. nights = 2 (StartDate +10, EndDate +12) para que la unitarizacion de hotel divida.
    private static async Task<int> SeedQuoteWithItemAsync(
        AppDbContext context, string serviceType, int? rateId, int? itemSupplierId,
        decimal totalCost = 200m, decimal totalPrice = 300m, int quantity = 1)
    {
        var quote = new Quote
        {
            QuoteNumber = "COT-CONV",
            Title = "Conversion catalogo",
            TravelStartDate = DateTime.UtcNow.Date.AddDays(10),
            TravelEndDate = DateTime.UtcNow.Date.AddDays(12),
            Adults = 2,
            Children = 0,
            TotalCost = totalCost,
            TotalSale = totalPrice
        };
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();

        // TotalCost/TotalPrice de QuoteItem son COMPUTADOS (UnitCost*Quantity); aca quantity=1 -> el total
        // coincide con el unitario que seteamos.
        var item = new QuoteItem
        {
            QuoteId = quote.Id,
            ServiceType = serviceType,
            Description = "Item de prueba",
            Quantity = quantity,
            SupplierId = itemSupplierId,
            RateId = rateId,
            UnitCost = totalCost,
            UnitPrice = totalPrice
        };
        context.QuoteItems.Add(item);
        await context.SaveChangesAsync();
        return quote.Id;
    }

    private static async Task<Rate> SeedRateAsync(AppDbContext context, int supplierId, string serviceType)
    {
        var rate = new Rate
        {
            SupplierId = supplierId,
            ServiceType = serviceType,
            ProductName = "Producto tarifado",
            HotelName = "Hotel tarifado",
            City = "Posadas",
            SearchName = "producto tarifado",
            NetCost = 100m,
            SalePrice = 150m,
            Currency = "ARS",
            IsActive = true
        };
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return rate;
    }

    [Fact]
    public async Task ConvertToFile_FlagOn_HotelWithRateAndSupplier_UpsertsSale()
    {
        await using var context = CreateContext();
        var rate = await SeedRateAsync(context, supplierId: 5, serviceType: "Hotel");
        var quoteId = await SeedQuoteWithItemAsync(context, "Hotel", rate.Id, itemSupplierId: 5,
            totalCost: 200m, totalPrice: 300m, quantity: 1);
        var service = CreateService(context, flagOn: true);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        Assert.True(reservaId > 0);
        var quote = await context.Quotes.SingleAsync(q => q.Id == quoteId);
        Assert.Equal(reservaId, quote.ConvertedReservaId); // la conversion quedo commiteada

        var sale = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(rate.Id, sale.RateId);
        Assert.Equal(5, sale.SupplierId);
        Assert.Equal(100m, sale.LastNetCost); // 200 total / (2 noches x 1 habitacion)
        Assert.Equal(1, sale.SalesCount);
    }

    [Fact]
    public async Task ConvertToFile_FlagOff_HotelWithRate_NoUpsert()
    {
        await using var context = CreateContext();
        var rate = await SeedRateAsync(context, supplierId: 5, serviceType: "Hotel");
        var quoteId = await SeedQuoteWithItemAsync(context, "Hotel", rate.Id, itemSupplierId: 5);
        var service = CreateService(context, flagOn: false);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        Assert.True(reservaId > 0); // la conversion funciona igual (byte-identico al historico)
        Assert.Equal(0, await context.RateSupplierSales.CountAsync()); // pero flag OFF nunca escribe la sugerencia
    }

    [Fact]
    public async Task ConvertToFile_FlagOn_HotelWithoutSupplier_SkipsUpsert()
    {
        await using var context = CreateContext();
        // Rate sin operador (SupplierId 0) y el item tampoco lo informa -> SupplierId efectivo 0.
        var rate = await SeedRateAsync(context, supplierId: 0, serviceType: "Hotel");
        var quoteId = await SeedQuoteWithItemAsync(context, "Hotel", rate.Id, itemSupplierId: null);
        var service = CreateService(context, flagOn: true);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        Assert.True(reservaId > 0);
        // El helper de upsert se saltea silenciosamente supplier <= 0 (evita FK rota / filas basura).
        Assert.Equal(0, await context.RateSupplierSales.CountAsync());
    }

    [Fact]
    public async Task ConvertToFile_FlagOn_AssistanceItem_FallsToGeneric_NoUpsert()
    {
        await using var context = CreateContext();
        // "asistencia" NO tiene rama tipada en la conversion: cae al ServicioReserva generico, que no
        // snapshotea Rate -> no entra a la lista de upserts aunque tenga RateId y operador.
        var rate = await SeedRateAsync(context, supplierId: 5, serviceType: "Asistencia");
        var quoteId = await SeedQuoteWithItemAsync(context, "asistencia", rate.Id, itemSupplierId: 5);
        var service = CreateService(context, flagOn: true);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        Assert.True(reservaId > 0);
        Assert.Equal(0, await context.RateSupplierSales.CountAsync());
    }
}
