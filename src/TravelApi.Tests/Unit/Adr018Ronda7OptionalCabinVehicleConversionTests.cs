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
/// ADR-018 Ronda 7 (guia UX, 2026-06-06): Cabina (Aereo) y Tipo de vehiculo (Traslado) son OPCIONALES
/// y el sistema NUNCA fabrica un valor que el vendedor no eligio (vacio -> null persistido).
///
/// <para>La ficha de carga ya cumplia (Adr018ProductFirstReconciliationTests); aca se cubren los DOS
/// paths que habian quedado afuera: (1) la conversion de presupuesto a reserva
/// (<c>QuoteService.ConvertToFileAsync</c>), que coalescia "Economy"/"Private" cuando la tarifa no
/// traia el dato ("Private" ni siquiera existe en el set documentado Sedan/Van/Minibus/Bus); y
/// (2) el alta de tramos del servicio generico (<c>ServicioReservaService.CreateSegmentAsync</c>),
/// que podia persistir "" porque el request no se normalizaba.</para>
/// </summary>
public class Adr018Ronda7OptionalCabinVehicleConversionTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // Flag de catalogo OFF: estos tests son del invariante "opcional => null", no del upsert de catalogo
    // (eso ya lo cubre QuoteServiceConvertCatalogTests). Con OFF la conversion es el path historico.
    private static QuoteService CreateQuoteService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = false });

        return new QuoteService(
            context,
            Mock.Of<IEntityReferenceResolver>(),
            settings.Object);
    }

    // Presupuesto con UN item del tipo pedido, opcionalmente ligado a una tarifa.
    private static async Task<int> SeedQuoteWithItemAsync(
        AppDbContext context, string serviceType, int? rateId, int? itemSupplierId)
    {
        var quote = new Quote
        {
            QuoteNumber = "COT-R7",
            Title = "Ronda 7 opcionales",
            Status = QuoteStatus.Accepted,
            TravelStartDate = DateTime.UtcNow.Date.AddDays(10),
            TravelEndDate = DateTime.UtcNow.Date.AddDays(12),
            Adults = 2,
            Children = 0,
            TotalCost = 200m,
            TotalSale = 300m
        };
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();

        var item = new QuoteItem
        {
            QuoteId = quote.Id,
            ServiceType = serviceType,
            Description = "Item de prueba",
            Quantity = 1,
            SupplierId = itemSupplierId,
            RateId = rateId,
            UnitCost = 200m,
            UnitPrice = 300m
        };
        context.QuoteItems.Add(item);
        await context.SaveChangesAsync();
        return quote.Id;
    }

    private static async Task<Rate> SeedRateAsync(
        AppDbContext context, string serviceType, string? cabinClass = null, string? vehicleType = null)
    {
        var rate = new Rate
        {
            SupplierId = 5,
            ServiceType = serviceType,
            ProductName = "Producto tarifado",
            SearchName = "producto tarifado",
            NetCost = 100m,
            SalePrice = 150m,
            Currency = "ARS",
            IsActive = true,
            CabinClass = cabinClass,
            VehicleType = vehicleType
        };
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return rate;
    }

    // ─── Conversion presupuesto -> reserva: vuelo ─────────────────────────────

    [Fact]
    public async Task ConvertToFile_FlightRateWithoutCabin_PersistsNull_NotEconomy()
    {
        await using var context = CreateContext();
        var rate = await SeedRateAsync(context, "Aereo", cabinClass: null);
        var quoteId = await SeedQuoteWithItemAsync(context, "aereo", rate.Id, itemSupplierId: 5);
        var service = CreateQuoteService(context);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        var segment = await context.Set<FlightSegment>().SingleAsync(s => s.ReservaId == reservaId);
        // Antes este path coalescia a "Economy" — fabricaba una cabina que nadie eligio.
        Assert.Null(segment.CabinClass);
    }

    [Fact]
    public async Task ConvertToFile_FlightWithoutRate_PersistsNullCabin()
    {
        await using var context = CreateContext();
        // Item sin tarifa asociada (carga manual del presupuesto): rate == null en la conversion.
        var quoteId = await SeedQuoteWithItemAsync(context, "vuelo", rateId: null, itemSupplierId: 5);
        var service = CreateQuoteService(context);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        var segment = await context.Set<FlightSegment>().SingleAsync(s => s.ReservaId == reservaId);
        Assert.Null(segment.CabinClass);
    }

    [Fact]
    public async Task ConvertToFile_FlightRateWithCabin_PersistsTrimmedValue()
    {
        await using var context = CreateContext();
        // El valor elegido se respeta (y se trimea, mismo criterio que la ficha de carga).
        var rate = await SeedRateAsync(context, "Aereo", cabinClass: "  Business  ");
        var quoteId = await SeedQuoteWithItemAsync(context, "aereo", rate.Id, itemSupplierId: 5);
        var service = CreateQuoteService(context);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        var segment = await context.Set<FlightSegment>().SingleAsync(s => s.ReservaId == reservaId);
        Assert.Equal("Business", segment.CabinClass);
    }

    // ─── Conversion presupuesto -> reserva: traslado ──────────────────────────

    [Fact]
    public async Task ConvertToFile_TransferRateWithoutVehicle_PersistsNull_NotPrivate()
    {
        await using var context = CreateContext();
        var rate = await SeedRateAsync(context, "Traslado", vehicleType: null);
        var quoteId = await SeedQuoteWithItemAsync(context, "traslado", rate.Id, itemSupplierId: 5);
        var service = CreateQuoteService(context);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        var transfer = await context.Set<TransferBooking>().SingleAsync(t => t.ReservaId == reservaId);
        // Antes este path coalescia a "Private", un valor que ni existe en el set Sedan/Van/Minibus/Bus.
        Assert.Null(transfer.VehicleType);
    }

    [Fact]
    public async Task ConvertToFile_TransferRateWithVehicle_PersistsValue()
    {
        await using var context = CreateContext();
        var rate = await SeedRateAsync(context, "Traslado", vehicleType: "Van");
        var quoteId = await SeedQuoteWithItemAsync(context, "traslado", rate.Id, itemSupplierId: 5);
        var service = CreateQuoteService(context);

        var reservaId = await service.ConvertToFileAsync(quoteId, CancellationToken.None);

        var transfer = await context.Set<TransferBooking>().SingleAsync(t => t.ReservaId == reservaId);
        Assert.Equal("Van", transfer.VehicleType);
    }

    // ─── Tramos del servicio generico (ServicioReservaService.CreateSegmentAsync) ─

    private static async Task<int> SeedServicioAsync(AppDbContext context)
    {
        var servicio = new ServicioReserva { Description = "Servicio generico de prueba" };
        context.Servicios.Add(servicio);
        await context.SaveChangesAsync();
        return servicio.Id;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CreateSegment_CabinEmptyOrNull_PersistsNull(string? cabinClass)
    {
        await using var context = CreateContext();
        var servicioId = await SeedServicioAsync(context);
        var service = new ServicioReservaService(context);

        var created = await service.CreateSegmentAsync(servicioId, new FlightSegment
        {
            CabinClass = cabinClass,
            DepartureTime = DateTime.UtcNow.AddDays(5),
            ArrivalTime = DateTime.UtcNow.AddDays(5).AddHours(2)
        }, CancellationToken.None);

        // El invariante vive en el service (no en el controller) para que CUALQUIER caller lo respete:
        // nunca se persiste "" como cabina, siempre null = "Sin especificar".
        var persisted = await context.FlightSegments.SingleAsync(s => s.Id == created.Id);
        Assert.Null(persisted.CabinClass);
    }

    [Fact]
    public async Task CreateSegment_CabinWithValue_PersistsTrimmedValue()
    {
        await using var context = CreateContext();
        var servicioId = await SeedServicioAsync(context);
        var service = new ServicioReservaService(context);

        var created = await service.CreateSegmentAsync(servicioId, new FlightSegment
        {
            CabinClass = " First ",
            DepartureTime = DateTime.UtcNow.AddDays(5),
            ArrivalTime = DateTime.UtcNow.AddDays(5).AddHours(2)
        }, CancellationToken.None);

        var persisted = await context.FlightSegments.SingleAsync(s => s.Id == created.Id);
        Assert.Equal("First", persisted.CabinClass);
    }
}
