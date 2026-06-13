using System.Collections.Generic;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.3 (catalogo find-or-create, "corazon"): cubre el path con flag ON — creacion inline,
/// find-or-create defensivo (R3), regla "request manda" (B1), cadena de costo D7 / "costo a confirmar"
/// (R11), upsert de RateSupplierSale, el boton "Confirmar costo" (D8c) y el byte-identico con flag OFF (R4).
///
/// <para>El upsert real (ON CONFLICT) corre en Postgres; aca (InMemory) el helper cae a su rama EF, asi que
/// la concurrencia (R10) se prueba en el VPS. La logica de negocio se valida toda aca.</para>
/// </summary>
public class BookingServiceCatalogTests
{
    private const string SeeCostPermission = Permissions.CobranzasSeeCost;

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static BookingService CreateService(
        AppDbContext context, IMapper mapper, bool flagOn, bool canSeeCost, int staleDays = 60)
        => BuildService(context, mapper, flagOn, canSeeCost, staleDays, out _, out _);

    // Igual que CreateService pero DEVUELVE los mocks de saldo (supplier/reserva) para poder verificar
    // que el alta y la confirmacion de costo los refrescan (B1). Los call-sites que no los necesitan usan
    // la sobrecarga CreateService de arriba (que descarta los out con _).
    private static BookingService BuildService(
        AppDbContext context, IMapper mapper, bool flagOn, bool canSeeCost, int staleDays,
        out Mock<ISupplierService> supplierService, out Mock<IReservaService> reservaService)
    {
        var reservaServiceMock = new Mock<IReservaService>();
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        // ADR-027: overload nuevo que pasan los paths de edicion (marca "confirmada con cambios").
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierServiceMock = new Mock<ISupplierService>();
        supplierServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
        var accessor = BuildHttpContextAccessor(userId);
        var resolver = canSeeCost ? BuildResolver(userId, SeeCostPermission) : BuildResolver(userId);

        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableCatalogFindOrCreate = flagOn,
                StaleCostReferenceDays = staleDays
            });

        supplierService = supplierServiceMock;
        reservaService = reservaServiceMock;

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaServiceMock.Object,
            supplierServiceMock.Object,
            context,
            mapper,
            NullLogger<BookingService>.Instance,
            resolver,
            accessor,
            settings.Object);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static async Task<(Reserva reserva, Supplier supplierA, Supplier supplierB)> SeedAsync(AppDbContext context)
    {
        var supplierA = new Supplier { Id = 1, Name = "Operador A" };
        var supplierB = new Supplier { Id = 2, Name = "Operador B" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-CAT", Name = "Reserva catalogo" };
        context.Suppliers.AddRange(supplierA, supplierB);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplierA, supplierB);
    }

    private static CreateHotelRequest HotelWithNewProduct(
        string supplierPublicId, string name, string? city, decimal net, decimal sale, decimal tax = 0m,
        int nights = 2, int rooms = 1)
        => new(
            SupplierId: supplierPublicId, HotelName: name, StarRating: 4, City: city ?? "", Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(10 + nights),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: rooms, ConfirmationNumber: null,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax, Notes: null,
            Tax: tax, Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest(name, city, supplierPublicId));

    private static CreateHotelRequest HotelWithRate(
        string supplierPublicId, string rateId, decimal net, decimal sale, decimal tax = 0m, int nights = 2, int rooms = 1)
        => new(
            SupplierId: supplierPublicId, HotelName: "Hotel Maitei", StarRating: 4, City: "Posadas", Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(10 + nights),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: rooms, ConfirmationNumber: null,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax, Notes: null,
            RateId: rateId, Tax: tax, Currency: "ARS");

    private static async Task<Rate> SeedHotelRateAsync(
        AppDbContext context, int supplierId, decimal unitNet = 100m, decimal unitTax = 15m, string currency = "ARS",
        DateTime? updatedAt = null, string searchName = "hotel maitei", string city = "Posadas")
    {
        var rate = new Rate
        {
            SupplierId = supplierId,
            ServiceType = "Hotel",
            ProductName = "Hotel Maitei",
            HotelName = "Hotel Maitei",
            SearchName = searchName,
            City = city,
            RoomType = "Doble",
            MealPlan = "Desayuno",
            NetCost = unitNet,
            Tax = unitTax,
            SalePrice = 160m,
            Currency = currency,
            PriceUnit = "noche_habitacion",
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = updatedAt
        };
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return rate;
    }

    // ===================== R3 — find-or-create defensivo =====================

    [Fact]
    public async Task NewProduct_SameNameAndCity_Twice_CreatesSingleRate()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        await service.CreateHotelAsync(reserva.Id, HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", 200m, 300m), CancellationToken.None);
        await service.CreateHotelAsync(reserva.Id, HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel  MAITEI ", "posadas", 220m, 320m), CancellationToken.None);

        Assert.Equal(1, await context.Rates.CountAsync()); // mismo SearchName + City normalizados -> reuso
    }

    [Fact]
    public async Task NewProduct_SameNameDifferentCity_CreatesTwoRates()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        await service.CreateHotelAsync(reserva.Id, HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Costanera", "Posadas", 200m, 300m), CancellationToken.None);
        await service.CreateHotelAsync(reserva.Id, HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Costanera", "Corrientes", 210m, 310m), CancellationToken.None);

        Assert.Equal(2, await context.Rates.CountAsync());
    }

    [Fact]
    public async Task NewProduct_DifferentSupplier_SameProduct_OneRateTwoSalesRows()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, supplierB) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        await service.CreateHotelAsync(reserva.Id, HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", 200m, 300m), CancellationToken.None);
        await service.CreateHotelAsync(reserva.Id, HotelWithNewProduct(supplierB.PublicId.ToString(), "Hotel Maitei", "Posadas", 220m, 320m), CancellationToken.None);

        Assert.Equal(1, await context.Rates.CountAsync());                 // un solo producto (supplier-agnostico)
        Assert.Equal(2, await context.RateSupplierSales.CountAsync());     // una fila por combinacion (Rate, supplier)
    }

    // ===================== B1 — request manda (Flight) =====================

    private static async Task<Rate> SeedFlightRateAsync(AppDbContext context, int supplierId)
    {
        var rate = new Rate
        {
            SupplierId = supplierId, ServiceType = "Aereo", ProductName = "EZE-BRC",
            SearchName = "eze-brc", NetCost = 300m, Tax = 30m, SalePrice = 500m, Commission = 170m,
            Currency = "ARS", IsActive = true
        };
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return rate;
    }

    private static CreateFlightRequest FlightWithRate(string supplierPublicId, string rateId, decimal net, decimal sale, decimal tax)
        => new(
            SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass: "Economy", Baggage: null, PNR: null,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax, Tax: tax, Notes: null,
            RateId: rateId, Currency: "ARS");

    [Fact]
    public async Task Flight_WithRate_FlagOn_RequestWins()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, supplierB) = await SeedAsync(context);
        var rate = await SeedFlightRateAsync(context, supplierA.Id);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        // Request trae precios DISTINTOS al rate (700/900) y OTRO operador (B).
        await service.CreateFlightAsync(reserva.Id, FlightWithRate(supplierB.PublicId.ToString(), rate.PublicId.ToString(), net: 700m, sale: 900m, tax: 40m), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(700m, stored.NetCost);     // request manda (NO el snapshot del rate)
        Assert.Equal(40m, stored.Tax);
        Assert.Equal(supplierB.Id, stored.SupplierId);
        Assert.Equal(rate.Id, stored.RateId);   // identidad del rate igual queda vinculada
    }

    [Fact]
    public async Task Flight_WithRate_FlagOff_SnapshotWins()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, supplierB) = await SeedAsync(context);
        var rate = await SeedFlightRateAsync(context, supplierA.Id);
        var service = CreateService(context, mapper, flagOn: false, canSeeCost: true);

        await service.CreateFlightAsync(reserva.Id, FlightWithRate(supplierB.PublicId.ToString(), rate.PublicId.ToString(), net: 700m, sale: 900m, tax: 40m), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(300m, stored.NetCost);     // flag OFF: el snapshot del rate PISA (comportamiento historico)
        Assert.Equal(30m, stored.Tax);
        Assert.Equal(0, await context.RateSupplierSales.CountAsync()); // OFF nunca escribe RateSupplierSale
    }

    // ===================== R4 — flag OFF byte-identico =====================

    [Fact]
    public async Task FlagOff_NewProductIgnored_NoRateNoSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: false, canSeeCost: true);

        await service.CreateHotelAsync(reserva.Id, HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", 200m, 300m), CancellationToken.None);

        Assert.Equal(0, await context.Rates.CountAsync());            // NewCatalogProduct se ignora con flag OFF
        Assert.Equal(0, await context.RateSupplierSales.CountAsync());
        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Null(hotel.Currency); // Currency null ignorada (el map la ignora y OFF no la asigna sin rate)
    }

    // ===================== validaciones de entrada (flag ON) =====================

    [Fact]
    public async Task FlagOn_MissingCurrency_Throws()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        var req = HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", 200m, 300m) with { Currency = null };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateHotelAsync(reserva.Id, req, CancellationToken.None));
    }

    [Fact]
    public async Task FlagOn_RateIdAndNewProductTogether_Throws()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var rate = await SeedHotelRateAsync(context, supplierA.Id);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        var req = HotelWithRate(supplierA.PublicId.ToString(), rate.PublicId.ToString(), 200m, 300m)
            with { NewCatalogProduct = new NewCatalogProductRequest("Hotel Maitei", "Posadas", supplierA.PublicId.ToString()) };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateHotelAsync(reserva.Id, req, CancellationToken.None));
    }

    [Fact]
    public async Task FlagOn_NewHotelWithoutCity_Throws()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        var req = HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", city: "  ", net: 200m, sale: 300m);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateHotelAsync(reserva.Id, req, CancellationToken.None));
    }

    // ===================== R6/R3 — unitarizacion del producto nuevo =====================

    [Fact]
    public async Task NewProduct_WithSeeCost_StoresUnitPricesAndSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        // 7 noches x 2 habitaciones = 14 unidades; total neto 1400 -> 100 por noche/habitacion.
        await service.CreateHotelAsync(reserva.Id,
            HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: 1400m, sale: 2100m, tax: 280m, nights: 7, rooms: 2),
            CancellationToken.None);

        var rate = await context.Rates.SingleAsync();
        Assert.True(rate.CreatedInSale);
        Assert.Equal(reserva.Id, rate.CreatedFromReservaId);
        Assert.Equal(100m, rate.NetCost);   // unitario
        Assert.Equal(20m, rate.Tax);
        Assert.Equal(150m, rate.SalePrice);

        var sale = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(100m, sale.LastNetCost);
        Assert.Equal("noche_habitacion", sale.LastPriceUnit);
        Assert.Equal(1, sale.SalesCount);
        Assert.Equal("ARS", sale.LastCurrency);
    }

    // ===================== R11 — cadena D7 / costo a confirmar =====================

    [Fact]
    public async Task MaskedCaller_NewProduct_MarksNoKnownCost_NoSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: false);

        // El front de un caller sin ver-costos manda net/tax = 0 (enmascarado).
        var req = HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: 0m, sale: 300m) with { Tax = 0m, Commission = 300m };
        await service.CreateHotelAsync(reserva.Id, req, CancellationToken.None);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.True(hotel.CostToConfirm);
        Assert.Equal("NoKnownCost", hotel.CostToConfirmReason);
        Assert.Equal(0m, hotel.NetCost);
        Assert.Equal(300m, hotel.Commission); // canonica: 300 - 0 - 0
        Assert.Equal(0, await context.RateSupplierSales.CountAsync()); // marcado -> NO upsertea
        var rate = await context.Rates.SingleAsync();
        Assert.Equal(0m, rate.NetCost); // el Rate nace en 0 y queda asi (nota 4)
    }

    [Fact]
    public async Task MaskedCaller_WithRate_FreshSameCurrency_ResolvesCostNoMark_AndUpserts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var rate = await SeedHotelRateAsync(context, supplierA.Id, unitNet: 100m, unitTax: 15m, updatedAt: DateTime.UtcNow);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: false);

        // 2 noches x 1 habitacion; el caller manda 0 (enmascarado) -> el server repone desde el rate.
        await service.CreateHotelAsync(reserva.Id,
            HotelWithRate(supplierA.PublicId.ToString(), rate.PublicId.ToString(), net: 0m, sale: 400m, tax: 0m, nights: 2, rooms: 1) with { Commission = 400m },
            CancellationToken.None);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.False(hotel.CostToConfirm);
        Assert.Equal(200m, hotel.NetCost); // 100 unit x 2 noches
        Assert.Equal(30m, hotel.Tax);
        Assert.Equal(170m, hotel.Commission); // 400 - 200 - 30
        var sale = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(100m, sale.LastNetCost); // re-unitarizado, NO envenenado con 0
    }

    [Fact]
    public async Task MaskedCaller_WithRate_NoUsableCost_MarksNoKnownCost_NoSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var rate = await SeedHotelRateAsync(context, supplierA.Id, unitNet: 0m, unitTax: 0m, updatedAt: DateTime.UtcNow);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: false);

        await service.CreateHotelAsync(reserva.Id,
            HotelWithRate(supplierA.PublicId.ToString(), rate.PublicId.ToString(), net: 0m, sale: 400m) with { Commission = 400m },
            CancellationToken.None);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.True(hotel.CostToConfirm);
        Assert.Equal("NoKnownCost", hotel.CostToConfirmReason);
        Assert.Equal(0, await context.RateSupplierSales.CountAsync());
    }

    [Fact]
    public async Task MaskedCaller_WithRate_StaleReference_MarksStale_NoSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        // Referencia vieja: UpdatedAt hace 100 dias, umbral default 60.
        var rate = await SeedHotelRateAsync(context, supplierA.Id, unitNet: 100m, unitTax: 15m, updatedAt: DateTime.UtcNow.AddDays(-100));
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: false);

        await service.CreateHotelAsync(reserva.Id,
            HotelWithRate(supplierA.PublicId.ToString(), rate.PublicId.ToString(), net: 0m, sale: 400m, nights: 2, rooms: 1) with { Commission = 400m },
            CancellationToken.None);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.True(hotel.CostToConfirm);
        Assert.Equal("StaleReference", hotel.CostToConfirmReason);
        Assert.Equal(200m, hotel.NetCost); // el costo SI se repone (referencia vieja pero existente)
        Assert.Equal(0, await context.RateSupplierSales.CountAsync()); // pero marcado -> no upsertea
    }

    // ===================== D8c — boton "Confirmar costo" =====================

    [Fact]
    public async Task ConfirmCost_OnMarkedHotel_CorrectsCost_ClearsMark_AndUpserts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var masked = CreateService(context, mapper, flagOn: true, canSeeCost: false);

        var created = await masked.CreateHotelAsync(reserva.Id,
            HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: 0m, sale: 400m, nights: 2, rooms: 1) with { Commission = 400m },
            CancellationToken.None);
        Assert.Equal(0, await context.RateSupplierSales.CountAsync()); // todavia no se registro

        var confirmer = CreateService(context, mapper, flagOn: true, canSeeCost: true);
        await confirmer.ConfirmHotelCostAsync(reserva.Id.ToString(), created.PublicId.ToString(),
            new ConfirmCostRequest(NetCost: 200m, Tax: 30m), CancellationToken.None);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.False(hotel.CostToConfirm);
        Assert.Null(hotel.CostToConfirmReason);
        Assert.Equal(200m, hotel.NetCost);
        Assert.Equal(30m, hotel.Tax);
        Assert.Equal(170m, hotel.Commission); // 400 - 200 - 30

        var sale = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(100m, sale.LastNetCost); // 200 total / (2 noches x 1 hab)
        Assert.Equal(hotel.CreatedAt, sale.LastSoldAt); // fecha de la VENTA, no de la confirmacion
    }

    [Fact]
    public async Task ConfirmCost_ConfirmingZero_IsValid_AndUpsertsZero()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var masked = CreateService(context, mapper, flagOn: true, canSeeCost: false);

        var created = await masked.CreateHotelAsync(reserva.Id,
            HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: 0m, sale: 400m) with { Commission = 400m },
            CancellationToken.None);

        var confirmer = CreateService(context, mapper, flagOn: true, canSeeCost: true);
        // Body vacio = confirmar el costo resuelto (0) tal cual.
        await confirmer.ConfirmHotelCostAsync(reserva.Id.ToString(), created.PublicId.ToString(), new ConfirmCostRequest(), CancellationToken.None);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.False(hotel.CostToConfirm);
        var sale = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(0m, sale.LastNetCost); // confirmar 0 vale: se registra como dato real
    }

    [Fact]
    public async Task ConfirmCost_OnUnmarkedService_IsNoOp()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        // Caller con permiso crea sin marca + ya upsertea una venta.
        var created = await service.CreateHotelAsync(reserva.Id,
            HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: 200m, sale: 400m, nights: 2, rooms: 1),
            CancellationToken.None);
        var before = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(1, before.SalesCount);

        await service.ConfirmHotelCostAsync(reserva.Id.ToString(), created.PublicId.ToString(), new ConfirmCostRequest(), CancellationToken.None);

        var after = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(1, after.SalesCount); // idempotente: no se duplica el upsert
    }

    [Fact]
    public async Task ConfirmCost_FlagOff_Throws404Signal()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        // Creamos un hotel marcado con flag ON, luego intentamos confirmar con flag OFF.
        var masked = CreateService(context, mapper, flagOn: true, canSeeCost: false);
        var created = await masked.CreateHotelAsync(reserva.Id,
            HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: 0m, sale: 400m) with { Commission = 400m },
            CancellationToken.None);

        var offService = CreateService(context, mapper, flagOn: false, canSeeCost: true);
        await Assert.ThrowsAsync<FeatureNotEnabledException>(() =>
            offService.ConfirmHotelCostAsync(reserva.Id.ToString(), created.PublicId.ToString(), new ConfirmCostRequest(), CancellationToken.None));
    }

    // ===================== B1 — confirm-cost refresca los saldos cacheados =====================

    [Fact]
    public async Task ConfirmCost_RefreshesSupplierAndReservaBalances()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);

        // Alta enmascarada de un producto nuevo -> queda "a confirmar" (NoKnownCost), con costo 0.
        var masked = CreateService(context, mapper, flagOn: true, canSeeCost: false);
        var created = await masked.CreateHotelAsync(reserva.Id,
            HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: 0m, sale: 400m, nights: 2, rooms: 1) with { Commission = 400m },
            CancellationToken.None);

        // El confirmador corrige 0 -> 200: esto cambia la deuda al operador, asi que el confirm DEBE
        // refrescar Supplier.CurrentBalance y el saldo de la reserva (regresion B1: antes no lo hacia).
        var confirmer = BuildService(context, mapper, flagOn: true, canSeeCost: true, 60, out var supplierMock, out var reservaMock);
        await confirmer.ConfirmHotelCostAsync(reserva.Id.ToString(), created.PublicId.ToString(),
            new ConfirmCostRequest(NetCost: 200m, Tax: 30m), CancellationToken.None);

        supplierMock.Verify(s => s.UpdateBalanceAsync(supplierA.Id, It.IsAny<CancellationToken>()), Times.Once);
        reservaMock.Verify(s => s.UpdateBalanceAsync(reserva.Id), Times.Once);
    }

    [Fact]
    public async Task ConfirmCost_OnUnmarkedService_DoesNotRefreshBalances()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);

        // Caller con permiso crea sin marca (ya confirmado de entrada).
        var created = await CreateService(context, mapper, flagOn: true, canSeeCost: true).CreateHotelAsync(reserva.Id,
            HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: 200m, sale: 400m, nights: 2, rooms: 1),
            CancellationToken.None);

        // Confirmar un servicio sin marca es no-op: NO debe tocar saldos (nada cambio).
        var confirmer = BuildService(context, mapper, flagOn: true, canSeeCost: true, 60, out var supplierMock, out var reservaMock);
        await confirmer.ConfirmHotelCostAsync(reserva.Id.ToString(), created.PublicId.ToString(), new ConfirmCostRequest(), CancellationToken.None);

        supplierMock.Verify(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        reservaMock.Verify(s => s.UpdateBalanceAsync(It.IsAny<int>()), Times.Never);
    }

    // ===================== Decision 1 — costos negativos rechazados (400) =====================

    [Fact]
    public async Task Create_SeeCost_NegativeNetCost_Throws()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        // Caller que ve costos: el request manda -> un costo negativo se rechaza (400 via ArgumentException).
        var req = HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: -5m, sale: 300m);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateHotelAsync(reserva.Id, req, CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmCost_NegativeNetCost_Throws()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var masked = CreateService(context, mapper, flagOn: true, canSeeCost: false);
        var created = await masked.CreateHotelAsync(reserva.Id,
            HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel Maitei", "Posadas", net: 0m, sale: 400m) with { Commission = 400m },
            CancellationToken.None);

        var confirmer = CreateService(context, mapper, flagOn: true, canSeeCost: true);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            confirmer.ConfirmHotelCostAsync(reserva.Id.ToString(), created.PublicId.ToString(),
                new ConfirmCostRequest(NetCost: -1m), CancellationToken.None));
    }

    // ===================== confirm-cost por tipo NO-Hotel (los 5 son casi-duplicados) =====================

    private static CreateFlightRequest FlightWithNewProduct(string supplierPublicId, string name, decimal net, decimal sale, decimal tax = 0m)
        => new(
            SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass: "Economy", Baggage: null, PNR: null,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax, Tax: tax, Notes: null,
            PassengerCount: 1, Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest(name, "Bariloche", supplierPublicId));

    private static CreateTransferRequest TransferWithNewProduct(string supplierPublicId, string name, decimal net, decimal sale, decimal tax = 0m)
        => new(
            SupplierId: supplierPublicId, PickupLocation: "Aeropuerto", DropoffLocation: "Hotel",
            PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Privado",
            Passengers: 2, IsRoundTrip: false, ReturnDateTime: null,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax, Notes: null,
            Tax: tax, Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest(name, "Bariloche", supplierPublicId));

    private static CreatePackageRequest PackageWithNewProduct(string supplierPublicId, string name, decimal net, decimal sale, decimal tax = 0m)
        => new(
            SupplierId: supplierPublicId, PackageName: name, Destination: "Caribe",
            StartDate: DateTime.UtcNow.Date.AddDays(10), EndDate: DateTime.UtcNow.Date.AddDays(17),
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax, Notes: null,
            Tax: tax, Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest(name, "Caribe", supplierPublicId));

    private static CreateAssistanceRequest AssistanceWithNewProduct(string supplierPublicId, string name, decimal net, decimal sale, decimal tax = 0m)
        => new(
            SupplierId: supplierPublicId,
            ValidFrom: DateTime.UtcNow.Date.AddDays(10), ValidTo: DateTime.UtcNow.Date.AddDays(17),
            Adults: 2, Children: 0,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax,
            Tax: tax, Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest(name, "Caribe", supplierPublicId));

    [Fact]
    public async Task ConfirmCost_OnMarkedFlight_CorrectsCost_ClearsMark_AndUpserts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var masked = CreateService(context, mapper, flagOn: true, canSeeCost: false);
        var created = await masked.CreateFlightAsync(reserva.Id,
            FlightWithNewProduct(supplierA.PublicId.ToString(), "EZE-BRC", net: 0m, sale: 900m), CancellationToken.None);

        var confirmer = CreateService(context, mapper, flagOn: true, canSeeCost: true);
        await confirmer.ConfirmFlightCostAsync(reserva.Id.ToString(), created.PublicId.ToString(),
            new ConfirmCostRequest(NetCost: 600m, Tax: 40m), CancellationToken.None);

        var flight = await context.FlightSegments.SingleAsync();
        Assert.False(flight.CostToConfirm);
        Assert.Equal(600m, flight.NetCost);
        Assert.Equal(40m, flight.Tax);
        Assert.Equal(260m, flight.Commission); // 900 - 600 - 40
        Assert.Equal(1, await context.RateSupplierSales.CountAsync()); // marcado -> recien al confirmar upsertea
    }

    [Fact]
    public async Task ConfirmCost_OnMarkedTransfer_CorrectsCost_ClearsMark_AndUpserts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var masked = CreateService(context, mapper, flagOn: true, canSeeCost: false);
        var created = await masked.CreateTransferAsync(reserva.Id,
            TransferWithNewProduct(supplierA.PublicId.ToString(), "Transfer EZE", net: 0m, sale: 100m), CancellationToken.None);

        var confirmer = CreateService(context, mapper, flagOn: true, canSeeCost: true);
        await confirmer.ConfirmTransferCostAsync(reserva.Id.ToString(), created.PublicId.ToString(),
            new ConfirmCostRequest(NetCost: 60m, Tax: 5m), CancellationToken.None);

        var transfer = await context.TransferBookings.SingleAsync();
        Assert.False(transfer.CostToConfirm);
        Assert.Equal(60m, transfer.NetCost);
        Assert.Equal(5m, transfer.Tax);
        Assert.Equal(1, await context.RateSupplierSales.CountAsync());
    }

    [Fact]
    public async Task ConfirmCost_OnMarkedPackage_CorrectsCost_ClearsMark_AndUpserts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var masked = CreateService(context, mapper, flagOn: true, canSeeCost: false);
        var created = await masked.CreatePackageAsync(reserva.Id,
            PackageWithNewProduct(supplierA.PublicId.ToString(), "Caribe Magico", net: 0m, sale: 2000m), CancellationToken.None);

        var confirmer = CreateService(context, mapper, flagOn: true, canSeeCost: true);
        await confirmer.ConfirmPackageCostAsync(reserva.Id.ToString(), created.PublicId.ToString(),
            new ConfirmCostRequest(NetCost: 1200m, Tax: 100m), CancellationToken.None);

        var package = await context.PackageBookings.SingleAsync();
        Assert.False(package.CostToConfirm);
        Assert.Equal(1200m, package.NetCost);
        Assert.Equal(100m, package.Tax);
        Assert.Equal(1, await context.RateSupplierSales.CountAsync());
    }

    [Fact]
    public async Task ConfirmCost_OnMarkedAssistance_CorrectsCost_ClearsMark_AndUpserts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var masked = CreateService(context, mapper, flagOn: true, canSeeCost: false);
        var created = await masked.CreateAssistanceAsync(reserva.Id,
            AssistanceWithNewProduct(supplierA.PublicId.ToString(), "Asistencia Plus", net: 0m, sale: 300m), CancellationToken.None);

        var confirmer = CreateService(context, mapper, flagOn: true, canSeeCost: true);
        await confirmer.ConfirmAssistanceCostAsync(reserva.Id.ToString(), created.PublicId.ToString(),
            new ConfirmCostRequest(NetCost: 140m, Tax: 0m), CancellationToken.None);

        var assistance = await context.AssistanceBookings.SingleAsync();
        Assert.False(assistance.CostToConfirm);
        Assert.Equal(140m, assistance.NetCost);
        Assert.Equal(1, await context.RateSupplierSales.CountAsync());
    }

    // ===================== request-manda + upsert por tipo NO-Flight (RateId, flag ON) =====================

    private static async Task<Rate> SeedSimpleRateAsync(
        AppDbContext context, int supplierId, string serviceType, string productName, string searchName)
    {
        var rate = new Rate
        {
            SupplierId = supplierId, ServiceType = serviceType, ProductName = productName,
            SearchName = searchName, NetCost = 50m, Tax = 5m, SalePrice = 90m, Commission = 35m,
            Currency = "ARS", IsActive = true
        };
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return rate;
    }

    private static CreateTransferRequest TransferWithRate(string supplierPublicId, string rateId, decimal net, decimal sale, decimal tax = 0m)
        => new(
            SupplierId: supplierPublicId, PickupLocation: "Aeropuerto", DropoffLocation: "Hotel",
            PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Privado",
            Passengers: 2, IsRoundTrip: false, ReturnDateTime: null,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax, Notes: null,
            RateId: rateId, Tax: tax, Currency: "ARS");

    private static CreatePackageRequest PackageWithRate(string supplierPublicId, string rateId, decimal net, decimal sale, decimal tax = 0m)
        => new(
            SupplierId: supplierPublicId, PackageName: "Caribe Magico", Destination: "Caribe",
            StartDate: DateTime.UtcNow.Date.AddDays(10), EndDate: DateTime.UtcNow.Date.AddDays(17),
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax, Notes: null,
            RateId: rateId, Tax: tax, Currency: "ARS");

    private static CreateAssistanceRequest AssistanceWithRate(string supplierPublicId, string rateId, decimal net, decimal sale, decimal tax = 0m)
        => new(
            SupplierId: supplierPublicId,
            ValidFrom: DateTime.UtcNow.Date.AddDays(10), ValidTo: DateTime.UtcNow.Date.AddDays(17),
            Adults: 2, Children: 0,
            NetCost: net, SalePrice: sale, Commission: sale - net - tax,
            RateId: rateId, Tax: tax, Currency: "ARS");

    [Fact]
    public async Task Transfer_WithRate_FlagOn_RequestWins_AndUpserts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, supplierB) = await SeedAsync(context);
        var rate = await SeedSimpleRateAsync(context, supplierA.Id, "Traslado", "Transfer EZE", "transfer eze");
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        await service.CreateTransferAsync(reserva.Id,
            TransferWithRate(supplierB.PublicId.ToString(), rate.PublicId.ToString(), net: 70m, sale: 120m, tax: 8m),
            CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        Assert.Equal(70m, stored.NetCost);   // request manda, no el snapshot del rate (50)
        Assert.Equal(8m, stored.Tax);
        Assert.Equal(supplierB.Id, stored.SupplierId);
        Assert.Equal(rate.Id, stored.RateId);
        var sale = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(supplierB.Id, sale.SupplierId); // la venta se registra para la combinacion (rate, operador B)
        Assert.Equal(70m, sale.LastNetCost);         // traslado: divisor 1 -> unitario == total
    }

    [Fact]
    public async Task Package_WithRate_FlagOn_RequestWins_AndUpserts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, supplierB) = await SeedAsync(context);
        var rate = await SeedSimpleRateAsync(context, supplierA.Id, "Paquete", "Caribe Magico", "caribe magico");
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        await service.CreatePackageAsync(reserva.Id,
            PackageWithRate(supplierB.PublicId.ToString(), rate.PublicId.ToString(), net: 1200m, sale: 2000m, tax: 100m),
            CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        Assert.Equal(1200m, stored.NetCost);
        Assert.Equal(supplierB.Id, stored.SupplierId);
        Assert.Equal(rate.Id, stored.RateId);
        var sale = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(supplierB.Id, sale.SupplierId);
        Assert.Equal(600m, sale.LastNetCost); // 2 pasajeros -> 1200 / 2
    }

    [Fact]
    public async Task Assistance_WithRate_FlagOn_RequestWins_AndUpserts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, supplierB) = await SeedAsync(context);
        var rate = await SeedSimpleRateAsync(context, supplierA.Id, "Asistencia", "Asistencia Plus", "asistencia plus");
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        await service.CreateAssistanceAsync(reserva.Id,
            AssistanceWithRate(supplierB.PublicId.ToString(), rate.PublicId.ToString(), net: 140m, sale: 300m, tax: 0m),
            CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        Assert.Equal(140m, stored.NetCost);
        Assert.Equal(supplierB.Id, stored.SupplierId);
        Assert.Equal(rate.Id, stored.RateId);
        var sale = await context.RateSupplierSales.SingleAsync();
        Assert.Equal(supplierB.Id, sale.SupplierId);
    }
}
