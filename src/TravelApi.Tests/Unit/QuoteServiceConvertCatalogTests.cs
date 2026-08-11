using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
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
            Status = QuoteStatus.Accepted,
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
        Assert.Equal(EstadoReserva.InManagement, (await context.Reservas.FindAsync(reservaId))!.Status);

        var sale = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(rate.Id, sale.RateId);
        Assert.Equal(5, sale.SupplierId);
        Assert.Equal(100m, sale.LastNetCost); // 200 total / (2 noches x 1 habitacion)
        Assert.Equal(1, sale.SalesCount);
    }

    [Fact]
    public async Task ConvertToFile_RejectsQuoteThatWasNotAccepted()
    {
        await using var context = CreateContext();
        var quoteId = await SeedQuoteWithItemAsync(context, "Hotel", rateId: null, itemSupplierId: null);
        var quote = await context.Quotes.FindAsync(quoteId);
        quote!.Status = QuoteStatus.Draft;
        await context.SaveChangesAsync();
        var service = CreateService(context, flagOn: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConvertToFileAsync(quoteId, CancellationToken.None));
    }

    [Fact]
    public async Task ConvertToFile_FlightItem_StartsAsRequested_NotConfirmed()
    {
        // Decision 2026-06-17: un vuelo que nace de convertir una cotizacion arranca "NN" (solicitado),
        // no "HK" (confirmado por la aerolinea). Sin TicketIssuedAt: no resuelve, no confirma la reserva.
        await using var context = CreateContext();
        var quoteId = await SeedQuoteWithItemAsync(context, "vuelo", rateId: null, itemSupplierId: null);
        var service = CreateService(context, flagOn: false);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        var flight = await context.Set<FlightSegment>().SingleAsync(f => f.ReservaId == reservaId);
        Assert.Equal("NN", flight.Status);
        Assert.Null(flight.TicketIssuedAt);
    }

    /// <summary>
    /// Al morir la llave del catalogo (spec firmada 2026-08-06, P8=A) la conversion de presupuesto
    /// SIEMPRE deja la memoria de venta, aunque los settings tengan la vieja bandera apagada. Ademas
    /// guarda de QUE reserva salio ese precio (M-1), que es lo que el Tarifario muestra como enlace.
    /// </summary>
    [Fact]
    public async Task ConvertToFile_SinLlave_HotelConTarifa_GuardaLaVentaConSuReserva()
    {
        await using var context = CreateContext();
        var rate = await SeedRateAsync(context, supplierId: 5, serviceType: "Hotel");
        var quoteId = await SeedQuoteWithItemAsync(context, "Hotel", rate.Id, itemSupplierId: 5);
        var service = CreateService(context, flagOn: false);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        Assert.True(reservaId > 0);
        var sale = Assert.Single(await context.RateSupplierSales.ToListAsync());
        Assert.Equal(rate.Id, sale.RateId);
        Assert.Equal(5, sale.SupplierId);
        Assert.Equal(reservaId, sale.LastReservaId);
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

    /// <summary>
    /// Cantidades minimas (QA con navegador en PROD, 2026-08-11): la conversion arma el hotel a mano,
    /// sin pasar por BookingService, asi que es un escritor mas que puede dejar un servicio con
    /// cantidades imposibles. Un item de presupuesto con cantidad 0 frena la conversion ENTERA con el
    /// mismo texto que ve el vendedor en la ficha de carga.
    /// </summary>
    [Fact]
    public async Task ConvertToFile_HotelItemWithZeroQuantity_IsRejected_WithTheUserText()
    {
        await using var context = CreateContext();
        var quoteId = await SeedQuoteWithItemAsync(context, "Hotel", rateId: null, itemSupplierId: null, quantity: 0);
        var service = CreateService(context, flagOn: false);

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.ConvertToFileAsync(quoteId, CancellationToken.None));

        Assert.Equal("Las habitaciones tienen que ser al menos 1.", error.Message);
        // Y la cotizacion NO queda marcada como convertida: el vinculo se escribe recien despues de
        // recorrer todos los items, asi que el rechazo la deja intacta para corregirla y reintentar.
        var quote = await context.Quotes.SingleAsync(q => q.Id == quoteId);
        Assert.Null(quote.ConvertedReservaId);
    }

    /// <summary>
    /// Gemelo del anterior por el otro lado del guard: las habitaciones estan bien, pero los
    /// pasajeros del presupuesto son imposibles.
    ///
    /// <para>Se usa MENORES en negativo y no "0 adultos" a proposito: la conversion tiene un
    /// fallback historico (<c>quote.Adults &gt; 0 ? quote.Adults : 2</c>) que tapa el cero de
    /// adultos, asi que ese camino no llega nunca al guard. Los menores, en cambio, se copian
    /// tal cual.</para>
    /// </summary>
    [Fact]
    public async Task ConvertToFile_QuoteWithImpossiblePassengers_IsRejected_WithTheUserText()
    {
        await using var context = CreateContext();
        var quoteId = await SeedQuoteWithItemAsync(context, "Hotel", rateId: null, itemSupplierId: null);
        var quote = await context.Quotes.FindAsync(quoteId);
        quote!.Adults = 2;
        quote.Children = -5;
        await context.SaveChangesAsync();
        var service = CreateService(context, flagOn: false);

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.ConvertToFileAsync(quoteId, CancellationToken.None));

        Assert.Equal("Los pasajeros tienen que ser al menos 1.", error.Message);
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
