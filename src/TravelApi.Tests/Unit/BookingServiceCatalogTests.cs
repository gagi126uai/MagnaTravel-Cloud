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

    // ===================== R4 — flag OFF byte-identico =====================

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

    // ===================== candado de tipo (2026-08-10) =====================
    // El buscador del tarifario ahora puede devolver resultados CRUZADOS entre tipos (buscando desde
    // la solapa Hotel puede aparecer un Paquete). Estos tests verifican el candado que impide guardar
    // un servicio tipado apuntando a un Rate de OTRO tipo de servicio.

    private static async Task<Rate> SeedPackageRateAsync(AppDbContext context, int supplierId)
    {
        var rate = new Rate
        {
            SupplierId = supplierId, ServiceType = "Paquete", ProductName = "Bariloche 7 noches",
            SearchName = "bariloche 7 noches", NetCost = 500m, Tax = 0m, SalePrice = 800m, Commission = 300m,
            Currency = "ARS", IsActive = true
        };
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return rate;
    }

    [Fact]
    public async Task CreateHotelAsync_WithRateIdFromDifferentServiceType_IsRejected()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        // El vendedor esta en la solapa Hotel pero el buscador le devolvio (y el eligio) un Paquete.
        var packageRate = await SeedPackageRateAsync(context, supplierA.Id);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        var req = HotelWithRate(supplierA.PublicId.ToString(), packageRate.PublicId.ToString(), 200m, 300m);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateHotelAsync(reserva.Id, req, CancellationToken.None));

        // El mensaje es para el vendedor: nada de nombres tecnicos ni el Id del producto (P-1).
        Assert.DoesNotContain("Rate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceType", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(packageRate.PublicId.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(packageRate.Id.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);

        // El candado corta ANTES de persistir: no queda ningun hotel a medio guardar.
        Assert.Equal(0, await context.HotelBookings.CountAsync());
    }

    [Fact]
    public async Task CreateHotelAsync_WithRateIdFromSameServiceType_IsAccepted()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var hotelRate = await SeedHotelRateAsync(context, supplierA.Id);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        var req = HotelWithRate(supplierA.PublicId.ToString(), hotelRate.PublicId.ToString(), 200m, 300m);

        var created = await service.CreateHotelAsync(reserva.Id, req, CancellationToken.None);

        Assert.NotNull(created);
        var stored = await context.HotelBookings.SingleAsync();
        Assert.Equal(hotelRate.Id, stored.RateId);
    }

    [Fact]
    public async Task UpdateHotelAsync_WithRateIdFromDifferentServiceType_IsRejected()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var hotelRate = await SeedHotelRateAsync(context, supplierA.Id);
        var packageRate = await SeedPackageRateAsync(context, supplierA.Id);
        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplierA.Id,
            RateId = hotelRate.Id,
            HotelName = "Hotel Maitei",
            City = "Posadas",
            CheckIn = DateTime.UtcNow.Date.AddDays(10),
            CheckOut = DateTime.UtcNow.Date.AddDays(12),
            Nights = 2,
            RoomType = "Doble",
            MealPlan = "Desayuno",
            Rooms = 1,
            Adults = 2,
            Children = 0,
            NetCost = 200m,
            SalePrice = 300m,
            Commission = 100m,
            Currency = "ARS",
        };
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);
        // El vendedor edita el hotel y, por error (o por un resultado cruzado del buscador), elige el
        // Paquete en vez de otro hotel.
        var request = new UpdateHotelRequest(
            supplierA.PublicId.ToString(),
            "Hotel Maitei",
            4,
            "Posadas",
            "Argentina",
            hotel.CheckIn,
            hotel.CheckOut,
            "Doble",
            "Desayuno",
            2,
            0,
            1,
            null,
            240m,
            888m,
            648m,
            "Solicitado",
            null,
            null,
            packageRate.PublicId.ToString(),
            "Solicitado");

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateHotelAsync(reserva.Id, hotel.Id, request, CancellationToken.None));

        Assert.DoesNotContain("Rate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceType", ex.Message, StringComparison.OrdinalIgnoreCase);

        // El RateId original NO se toco: el candado corta antes de guardar el cambio.
        var stored = await context.HotelBookings.SingleAsync();
        Assert.Equal(hotelRate.Id, stored.RateId);
    }

    // C-3b (review 2026-08-1x): el candado se probo entero solo para Hotel. Estos dos repiten
    // create+update para PAQUETE (al reves: un Hotel elegido donde iba un Paquete), para dejar
    // constancia de que el candado es el MISMO en los otros 4 tipos, no algo especial de Hotel.

    [Fact]
    public async Task CreatePackageAsync_WithRateIdFromDifferentServiceType_IsRejected()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        // El vendedor esta en la solapa Paquete pero el buscador le devolvio (y el eligio) un Hotel.
        var hotelRate = await SeedHotelRateAsync(context, supplierA.Id);
        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        var req = PackageWithRate(supplierA.PublicId.ToString(), hotelRate.PublicId.ToString(), 1200m, 2000m, tax: 100m);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreatePackageAsync(reserva.Id, req, CancellationToken.None));

        Assert.DoesNotContain("Rate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceType", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.PackageBookings.CountAsync());
    }

    [Fact]
    public async Task UpdatePackageAsync_WithRateIdFromDifferentServiceType_IsRejected()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var packageRate = await SeedPackageRateAsync(context, supplierA.Id);
        var hotelRate = await SeedHotelRateAsync(context, supplierA.Id);
        var package = new PackageBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplierA.Id,
            RateId = packageRate.Id,
            PackageName = "Caribe Magico",
            Destination = "Caribe",
            StartDate = DateTime.UtcNow.Date.AddDays(10),
            EndDate = DateTime.UtcNow.Date.AddDays(17),
            Adults = 2,
            Children = 0,
            NetCost = 500m,
            SalePrice = 800m,
            Commission = 300m,
            Currency = "ARS",
        };
        context.PackageBookings.Add(package);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);
        // El vendedor edita el paquete y, por un resultado cruzado del buscador, elige el Hotel.
        var request = new UpdatePackageRequest(
            SupplierId: supplierA.PublicId.ToString(),
            PackageName: "Caribe Magico",
            Destination: "Caribe",
            StartDate: package.StartDate,
            EndDate: package.EndDate,
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null, ConfirmationNumber: null,
            NetCost: 600m, SalePrice: 900m, Commission: 300m,
            Status: "Solicitado", Notes: null,
            RateId: hotelRate.PublicId.ToString());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdatePackageAsync(reserva.Id, package.Id, request, CancellationToken.None));

        Assert.DoesNotContain("Rate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceType", ex.Message, StringComparison.OrdinalIgnoreCase);

        // El RateId original NO se toco: el candado corta antes de guardar el cambio.
        var stored = await context.PackageBookings.SingleAsync();
        Assert.Equal(packageRate.Id, stored.RateId);
    }

    // ===================== coherencia identidad-del-producto vs datos-de-la-venta (review 2026-08-1x) =====================
    // Bug: cuando el hotel sigue vinculado al MISMO Rate (no cambio de producto), UpdateHotelAsync
    // revertia TODOS los campos derivados del snapshot — incluyendo operador, tipo de habitacion y
    // regimen, que la ficha de edicion SI deja tocar sin cambiar de producto. El vendedor los
    // cambiaba, el PUT contestaba 200 "guardado", y volvian solos en silencio. El fix separa la
    // IDENTIDAD del producto (RateId/HotelName/City/Country/StarRating — protegida, el buscador esta
    // deshabilitado en edicion) de los DATOS DE LA VENTA (SupplierId/RoomType/MealPlan — editables).

    private static async Task<HotelBooking> SeedLinkedHotelAsync(AppDbContext context, int reservaId, Rate rate)
    {
        var hotel = new HotelBooking
        {
            ReservaId = reservaId,
            SupplierId = rate.SupplierId ?? 0,
            RateId = rate.Id,
            HotelName = rate.HotelName!,
            City = rate.City!,
            Country = "Argentina",
            CheckIn = DateTime.UtcNow.Date.AddDays(10),
            CheckOut = DateTime.UtcNow.Date.AddDays(12),
            Nights = 2,
            RoomType = rate.RoomType!,
            MealPlan = rate.MealPlan!,
            Rooms = 1,
            Adults = 2,
            Children = 0,
            NetCost = 200m,
            SalePrice = 300m,
            Commission = 100m,
            Currency = "ARS",
        };
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();
        return hotel;
    }

    [Fact]
    public async Task UpdateHotelAsync_WithSameRateId_SavesOperatorRoomTypeAndMealPlanChanges()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, supplierB) = await SeedAsync(context);
        var hotelRate = await SeedHotelRateAsync(context, supplierA.Id); // Hotel Maitei, Posadas, Doble/Desayuno
        var hotel = await SeedLinkedHotelAsync(context, reserva.Id, hotelRate);

        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        // El vendedor NO cambia de producto (mismo RateId) pero SI cambia el operador, el tipo de
        // habitacion y el regimen: cosas que la ficha de edicion permite tocar sin buscar otro hotel.
        var request = new UpdateHotelRequest(
            SupplierId: supplierB.PublicId.ToString(),
            HotelName: "Hotel Maitei",
            StarRating: 4,
            City: "Posadas",
            Country: "Argentina",
            CheckIn: hotel.CheckIn,
            CheckOut: hotel.CheckOut,
            RoomType: "Triple",
            MealPlan: "Media pension",
            Adults: 2,
            Children: 0,
            Rooms: 1,
            ConfirmationNumber: null,
            NetCost: 200m,
            SalePrice: 300m,
            Commission: 100m,
            RateId: hotelRate.PublicId.ToString());

        await service.UpdateHotelAsync(reserva.Id, hotel.Id, request, CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        Assert.Equal(supplierB.Id, stored.SupplierId);
        Assert.Equal("Triple", stored.RoomType);
        Assert.Equal("Media pension", stored.MealPlan);
        // El producto sigue siendo el mismo: esto no es un cambio de identidad, solo de venta.
        Assert.Equal(hotelRate.Id, stored.RateId);
    }

    [Fact]
    public async Task UpdateHotelAsync_WithSameRateId_KeepsProductIdentityEvenIfRequestSendsOtherValues()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var hotelRate = await SeedHotelRateAsync(context, supplierA.Id); // Hotel Maitei, Posadas
        var hotel = await SeedLinkedHotelAsync(context, reserva.Id, hotelRate);

        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        // El request manda OTRO nombre y OTRA ciudad, pero el RateId es el MISMO: en la ficha real
        // esto no puede pasar (el buscador esta deshabilitado en edicion), asi que si llega hay que
        // protegerse (un request viejo, un cliente manipulado) y quedarse con la identidad guardada.
        var request = new UpdateHotelRequest(
            SupplierId: supplierA.PublicId.ToString(),
            HotelName: "Otro Hotel Cualquiera",
            StarRating: 5,
            City: "Buenos Aires",
            Country: "Argentina",
            CheckIn: hotel.CheckIn,
            CheckOut: hotel.CheckOut,
            RoomType: "Doble",
            MealPlan: "Desayuno",
            Adults: 2,
            Children: 0,
            Rooms: 1,
            ConfirmationNumber: null,
            NetCost: 200m,
            SalePrice: 300m,
            Commission: 100m,
            RateId: hotelRate.PublicId.ToString());

        await service.UpdateHotelAsync(reserva.Id, hotel.Id, request, CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        Assert.Equal("Hotel Maitei", stored.HotelName);
        Assert.Equal("Posadas", stored.City);
    }

    [Fact]
    public async Task UpdateHotelAsync_WithNewRateId_AppliesFullSnapshotOfNewProduct()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplierA, supplierB) = await SeedAsync(context);
        var oldRate = await SeedHotelRateAsync(context, supplierA.Id); // Hotel Maitei, Posadas
        var newRate = new Rate
        {
            SupplierId = supplierB.Id,
            ServiceType = "Hotel",
            ProductName = "Sheraton Iguazu",
            HotelName = "Sheraton Iguazu",
            SearchName = "sheraton iguazu",
            City = "Puerto Iguazu",
            RoomType = "Suite",
            MealPlan = "Todo incluido",
            StarRating = 5,
            NetCost = 150m,
            SalePrice = 250m,
            Currency = "USD",
            PriceUnit = "noche_habitacion",
            IsActive = true,
        };
        context.Rates.Add(newRate);
        await context.SaveChangesAsync();
        var hotel = await SeedLinkedHotelAsync(context, reserva.Id, oldRate);

        var service = CreateService(context, mapper, flagOn: true, canSeeCost: true);

        // El vendedor SI cambia de producto (busco otro hotel y lo eligio): el snapshot completo del
        // Rate nuevo tiene que pisar todo, igual que antes del fix.
        var request = new UpdateHotelRequest(
            SupplierId: supplierA.PublicId.ToString(), // da igual: el snapshot del rate nuevo manda
            HotelName: "Hotel Maitei",                 // el request todavia trae el nombre viejo
            StarRating: 3,
            City: "Posadas",
            Country: "Argentina",
            CheckIn: hotel.CheckIn,
            CheckOut: hotel.CheckOut,
            RoomType: "Doble",
            MealPlan: "Desayuno",
            Adults: 2,
            Children: 0,
            Rooms: 1,
            ConfirmationNumber: null,
            NetCost: 200m,
            SalePrice: 300m,
            Commission: 100m,
            RateId: newRate.PublicId.ToString());

        await service.UpdateHotelAsync(reserva.Id, hotel.Id, request, CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        Assert.Equal(newRate.Id, stored.RateId);
        Assert.Equal("Sheraton Iguazu", stored.HotelName);
        Assert.Equal("Puerto Iguazu", stored.City);
        Assert.Equal(supplierB.Id, stored.SupplierId);
        Assert.Equal("Suite", stored.RoomType);
        Assert.Equal("Todo incluido", stored.MealPlan);
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

    // ===================== La moneda de la venta es OBLIGATORIA (D5) =====================
    // Desde que murio la llave del catalogo (2026-08-06) los CINCO tipos de servicio corren por el mismo
    // camino, y ese camino exige la moneda: sin ella no se puede saber en que plata se vendio, y todo el
    // circuito multimoneda (saldo por moneda, deuda al operador, memoria de precios) queda envenenado.
    // El mensaje del rechazo es el que ve el vendedor; el controller lo devuelve como 400.

    [Fact]
    public async Task CreateHotel_SinMoneda_Rechaza()
    {
        await using var context = CreateContext();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), flagOn: true, canSeeCost: true);

        var request = HotelWithNewProduct(supplierA.PublicId.ToString(), "Hotel sin moneda", "Posadas", 200m, 300m)
            with { Currency = null };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateHotelAsync(reserva.Id, request, CancellationToken.None));
        Assert.Equal("La moneda de la venta es obligatoria.", error.Message);
        Assert.Equal(0, await context.HotelBookings.CountAsync());
    }

    [Fact]
    public async Task CreateFlight_SinMoneda_Rechaza()
    {
        await using var context = CreateContext();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), flagOn: true, canSeeCost: true);

        var request = FlightWithNewProduct(supplierA.PublicId.ToString(), "Vuelo sin moneda", 200m, 300m)
            with { Currency = "  " };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateFlightAsync(reserva.Id, request, CancellationToken.None));
        Assert.Equal("La moneda de la venta es obligatoria.", error.Message);
        Assert.Equal(0, await context.FlightSegments.CountAsync());
    }

    [Fact]
    public async Task CreateTransfer_SinMoneda_Rechaza()
    {
        await using var context = CreateContext();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), flagOn: true, canSeeCost: true);

        var request = TransferWithNewProduct(supplierA.PublicId.ToString(), "Traslado sin moneda", 50m, 80m)
            with { Currency = null };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateTransferAsync(reserva.Id, request, CancellationToken.None));
        Assert.Equal("La moneda de la venta es obligatoria.", error.Message);
        Assert.Equal(0, await context.TransferBookings.CountAsync());
    }

    [Fact]
    public async Task CreatePackage_SinMoneda_Rechaza()
    {
        await using var context = CreateContext();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), flagOn: true, canSeeCost: true);

        var request = PackageWithNewProduct(supplierA.PublicId.ToString(), "Paquete sin moneda", 800m, 1000m)
            with { Currency = null };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreatePackageAsync(reserva.Id, request, CancellationToken.None));
        Assert.Equal("La moneda de la venta es obligatoria.", error.Message);
        Assert.Equal(0, await context.PackageBookings.CountAsync());
    }

    [Fact]
    public async Task CreateAssistance_SinMoneda_Rechaza()
    {
        await using var context = CreateContext();
        var (reserva, supplierA, _) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), flagOn: true, canSeeCost: true);

        var request = AssistanceWithNewProduct(supplierA.PublicId.ToString(), "Asistencia sin moneda", 50m, 90m)
            with { Currency = null };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAssistanceAsync(reserva.Id, request, CancellationToken.None));
        Assert.Equal("La moneda de la venta es obligatoria.", error.Message);
        Assert.Equal(0, await context.AssistanceBookings.CountAsync());
    }

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
