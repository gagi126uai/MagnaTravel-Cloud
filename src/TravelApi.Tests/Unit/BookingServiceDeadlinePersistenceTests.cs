using System;
using System.Collections.Generic;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.4 (§2.2, R12): persistencia de fechas limite en el SERVICE (no solo el map).
///  - Create: el deadline que viene en el request se persiste normalizado a medianoche Kind=Utc.
///  - Update anti-clobber gobernado por DeadlinesSpecified: false (modal viejo) preserva el valor
///    persistido; true + valor lo actualiza; true + null lo borra.
/// Corre con el flag de catalogo OFF (path legacy): el anti-clobber del deadline es independiente del flag.
/// </summary>
public class BookingServiceDeadlinePersistenceTests
{
    private static AppDbContext CreateContext()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static BookingService CreateService(AppDbContext context, IMapper mapper, bool catalogEnabled = false)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        // Caller que VE costos (asi la resolucion de costos en update es trivial: request manda).
        var resolverMock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { Permissions.CobranzasSeeCost };
        resolverMock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);

        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = catalogEnabled });

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaService.Object,
            supplierService.Object,
            context,
            mapper,
            NullLogger<BookingService>.Instance,
            resolverMock.Object,
            accessor,
            settings.Object);
    }

    private static async Task<(Reserva reserva, Supplier supplier)> SeedAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Operador" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "Reserva", Status = EstadoReserva.Budget };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    private static readonly DateTime DeadlineValue = new(2026, 7, 10);
    private static readonly DateTime PersistedDeadline = DateTime.SpecifyKind(new DateTime(2026, 5, 1), DateTimeKind.Utc);

    // ===================== Create: persiste el deadline normalizado =====================

    [Fact]
    public async Task CreateHotel_PersistsDeadlineAsMidnightUtc()
    {
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper());

        var req = new CreateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Maitei", StarRating: 4, City: "Posadas", Country: "AR",
            CheckIn: new DateTime(2026, 9, 1), CheckOut: new DateTime(2026, 9, 5),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 100m, SalePrice: 200m, Commission: 100m, Notes: null,
            // El deadline viene con hora; debe persistirse date-only a medianoche Kind=Utc.
            OperatorPaymentDeadline: new DateTime(2026, 7, 10, 14, 30, 0));

        await service.CreateHotelAsync(1, req, CancellationToken.None);

        var hotel = await context.HotelBookings.FirstAsync();
        Assert.NotNull(hotel.OperatorPaymentDeadline);
        Assert.Equal(new DateTime(2026, 7, 10), hotel.OperatorPaymentDeadline!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, hotel.OperatorPaymentDeadline.Value.Kind);
        Assert.Equal(0, hotel.OperatorPaymentDeadline.Value.TimeOfDay.Ticks); // medianoche
    }

    [Fact]
    public async Task CreateFlight_PersistsDeadlineAsMidnightUtc()
    {
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper());

        var req = new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(), AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "100",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "MIA", DestinationCity: "Miami",
            DepartureTime: new DateTime(2026, 9, 1, 10, 0, 0), ArrivalTime: new DateTime(2026, 9, 1, 18, 0, 0),
            CabinClass: "Economy", Baggage: "23kg", PNR: "ABC123",
            NetCost: 100m, SalePrice: 200m, Commission: 90m, Tax: 10m, Notes: null,
            // El deadline de emision viene con hora; debe persistirse date-only a medianoche Kind=Utc.
            TicketingDeadline: new DateTime(2026, 7, 10, 14, 30, 0));

        await service.CreateFlightAsync(1, req, CancellationToken.None);

        var flight = await context.FlightSegments.FirstAsync();
        Assert.NotNull(flight.TicketingDeadline);
        Assert.Equal(new DateTime(2026, 7, 10), flight.TicketingDeadline!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, flight.TicketingDeadline.Value.Kind);
        Assert.Equal(0, flight.TicketingDeadline.Value.TimeOfDay.Ticks); // medianoche
    }

    [Fact]
    public async Task CreatePackage_PersistsDeadlineAsMidnightUtc()
    {
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper());

        var req = new CreatePackageRequest(
            SupplierId: supplier.PublicId.ToString(), PackageName: "Caribe", Destination: "Cancun",
            StartDate: new DateTime(2026, 9, 1), EndDate: new DateTime(2026, 9, 7),
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Notes: null,
            OperatorPaymentDeadline: new DateTime(2026, 7, 10, 14, 30, 0));

        await service.CreatePackageAsync(1, req, CancellationToken.None);

        var package = await context.PackageBookings.FirstAsync();
        Assert.NotNull(package.OperatorPaymentDeadline);
        Assert.Equal(new DateTime(2026, 7, 10), package.OperatorPaymentDeadline!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, package.OperatorPaymentDeadline.Value.Kind);
        Assert.Equal(0, package.OperatorPaymentDeadline.Value.TimeOfDay.Ticks); // medianoche
    }

    [Fact]
    public async Task CreateHotelWithCatalogEnabled_PersistsDeadlineAsMidnightUtc()
    {
        // El path de CATALOGO (flag ON) asigna el deadline igual que el legacy (CatalogCreates.cs:106). Verifica
        // que prender EnableCatalogFindOrCreate no rompe la persistencia/normalizacion de la fecha limite.
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogEnabled: true);

        var req = new CreateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Maitei", StarRating: 4, City: "Posadas", Country: "AR",
            CheckIn: new DateTime(2026, 9, 1), CheckOut: new DateTime(2026, 9, 5),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 100m, SalePrice: 200m, Commission: 100m, Notes: null,
            // Con flag ON la moneda es obligatoria; producto nuevo en linea para ejercitar find-or-create.
            Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest(
                Name: "Maitei", City: "Posadas", SupplierPublicId: supplier.PublicId.ToString()),
            OperatorPaymentDeadline: new DateTime(2026, 7, 10, 14, 30, 0));

        await service.CreateHotelAsync(1, req, CancellationToken.None);

        var hotel = await context.HotelBookings.FirstAsync();
        Assert.NotNull(hotel.OperatorPaymentDeadline);
        Assert.Equal(new DateTime(2026, 7, 10), hotel.OperatorPaymentDeadline!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, hotel.OperatorPaymentDeadline.Value.Kind);
    }

    // ===================== Update Hotel: anti-clobber =====================

    [Fact]
    public async Task UpdateHotel_WithoutDeadlineBlock_PreservesPersistedDeadline()
    {
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1, HotelName = "Maitei", City = "Posadas",
            CheckIn = new DateTime(2026, 9, 1), CheckOut = new DateTime(2026, 9, 5), Nights = 4,
            SalePrice = 200m, NetCost = 100m, OperatorPaymentDeadline = PersistedDeadline
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        // Request estilo modal viejo: NO manda el bloque de deadline (DeadlinesSpecified=false default).
        var req = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Maitei", StarRating: 4, City: "Posadas", Country: "AR",
            CheckIn: new DateTime(2026, 9, 1), CheckOut: new DateTime(2026, 9, 5),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1,
            ConfirmationNumber: null, NetCost: 100m, SalePrice: 200m, Commission: 100m, Status: "Solicitado", Notes: null);

        await service.UpdateHotelAsync(1, 1, req, CancellationToken.None);

        var hotel = await context.HotelBookings.FirstAsync();
        Assert.Equal(PersistedDeadline, hotel.OperatorPaymentDeadline);
    }

    [Fact]
    public async Task UpdateHotel_DeadlinesSpecifiedWithValue_UpdatesDeadline()
    {
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1, HotelName = "Maitei", City = "Posadas",
            CheckIn = new DateTime(2026, 9, 1), CheckOut = new DateTime(2026, 9, 5), Nights = 4,
            SalePrice = 200m, NetCost = 100m, OperatorPaymentDeadline = PersistedDeadline
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        var req = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Maitei", StarRating: 4, City: "Posadas", Country: "AR",
            CheckIn: new DateTime(2026, 9, 1), CheckOut: new DateTime(2026, 9, 5),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1,
            ConfirmationNumber: null, NetCost: 100m, SalePrice: 200m, Commission: 100m, Status: "Solicitado", Notes: null,
            OperatorPaymentDeadline: DeadlineValue, DeadlinesSpecified: true);

        await service.UpdateHotelAsync(1, 1, req, CancellationToken.None);

        var hotel = await context.HotelBookings.FirstAsync();
        Assert.Equal(DeadlineValue.Date, hotel.OperatorPaymentDeadline!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, hotel.OperatorPaymentDeadline.Value.Kind);
    }

    [Fact]
    public async Task UpdateHotel_DeadlinesSpecifiedWithNull_ClearsDeadline()
    {
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1, HotelName = "Maitei", City = "Posadas",
            CheckIn = new DateTime(2026, 9, 1), CheckOut = new DateTime(2026, 9, 5), Nights = 4,
            SalePrice = 200m, NetCost = 100m, OperatorPaymentDeadline = PersistedDeadline
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        var req = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Maitei", StarRating: 4, City: "Posadas", Country: "AR",
            CheckIn: new DateTime(2026, 9, 1), CheckOut: new DateTime(2026, 9, 5),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1,
            ConfirmationNumber: null, NetCost: 100m, SalePrice: 200m, Commission: 100m, Status: "Solicitado", Notes: null,
            OperatorPaymentDeadline: null, DeadlinesSpecified: true);

        await service.UpdateHotelAsync(1, 1, req, CancellationToken.None);

        var hotel = await context.HotelBookings.FirstAsync();
        Assert.Null(hotel.OperatorPaymentDeadline);
    }

    // ===================== Update Package: anti-clobber (preserva sin bloque, borra con true+null) =====================

    [Fact]
    public async Task UpdatePackage_WithoutBlock_Preserves_AndSpecifiedNull_Clears()
    {
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1, PackageName = "Caribe", Destination = "Cancun",
            StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 7), Nights = 6,
            SalePrice = 500m, NetCost = 300m, OperatorPaymentDeadline = PersistedDeadline
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        UpdatePackageRequest Build(DateTime? deadline, bool specified) => new(
            SupplierId: supplier.PublicId.ToString(), PackageName: "Caribe", Destination: "Cancun",
            StartDate: new DateTime(2026, 9, 1), EndDate: new DateTime(2026, 9, 7),
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null, ConfirmationNumber: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Status: "Solicitado", Notes: null,
            OperatorPaymentDeadline: deadline, DeadlinesSpecified: specified);

        // Sin bloque -> preserva.
        await service.UpdatePackageAsync(1, 1, Build(null, false), CancellationToken.None);
        Assert.Equal(PersistedDeadline, (await context.PackageBookings.FirstAsync()).OperatorPaymentDeadline);

        // Con bloque + null -> borra.
        await service.UpdatePackageAsync(1, 1, Build(null, true), CancellationToken.None);
        Assert.Null((await context.PackageBookings.FirstAsync()).OperatorPaymentDeadline);
    }

    [Fact]
    public async Task UpdatePackage_DeadlinesSpecifiedWithValue_UpdatesDeadline()
    {
        // Cierra el cuadrante asimetrico que faltaba: Paquete con bloque + VALOR actualiza (el otro test cubre
        // sin-bloque preserva + bloque+null borra). Normaliza a medianoche Kind=Utc.
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1, PackageName = "Caribe", Destination = "Cancun",
            StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 7), Nights = 6,
            SalePrice = 500m, NetCost = 300m, OperatorPaymentDeadline = PersistedDeadline
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        var req = new UpdatePackageRequest(
            SupplierId: supplier.PublicId.ToString(), PackageName: "Caribe", Destination: "Cancun",
            StartDate: new DateTime(2026, 9, 1), EndDate: new DateTime(2026, 9, 7),
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null, ConfirmationNumber: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Status: "Solicitado", Notes: null,
            OperatorPaymentDeadline: DeadlineValue, DeadlinesSpecified: true);

        await service.UpdatePackageAsync(1, 1, req, CancellationToken.None);

        var package = await context.PackageBookings.FirstAsync();
        Assert.Equal(DeadlineValue.Date, package.OperatorPaymentDeadline!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, package.OperatorPaymentDeadline.Value.Kind);
    }

    // ===================== Update Flight: anti-clobber =====================

    [Fact]
    public async Task UpdateFlight_WithoutBlock_Preserves_AndSpecifiedValue_Updates()
    {
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 1, ReservaId = 1, SupplierId = 1, AirlineCode = "AR", FlightNumber = "100",
            Origin = "EZE", Destination = "MIA",
            DepartureTime = DateTime.SpecifyKind(new DateTime(2026, 9, 1, 10, 0, 0), DateTimeKind.Utc),
            ArrivalTime = DateTime.SpecifyKind(new DateTime(2026, 9, 1, 18, 0, 0), DateTimeKind.Utc),
            SalePrice = 200m, NetCost = 100m, Status = "HK", TicketingDeadline = PersistedDeadline
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        UpdateFlightRequest Build(DateTime? deadline, bool specified) => new(
            SupplierId: supplier.PublicId.ToString(), AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "100",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "MIA", DestinationCity: "Miami",
            DepartureTime: new DateTime(2026, 9, 1, 10, 0, 0), ArrivalTime: new DateTime(2026, 9, 1, 18, 0, 0),
            CabinClass: "Economy", Baggage: "23kg", TicketNumber: null, PNR: "ABC123",
            NetCost: 100m, SalePrice: 200m, Commission: 90m, Tax: 10m, Status: "HK", Notes: null,
            TicketingDeadline: deadline, DeadlinesSpecified: specified);

        // Sin bloque -> preserva.
        await service.UpdateFlightAsync(1, 1, Build(null, false), CancellationToken.None);
        Assert.Equal(PersistedDeadline, (await context.FlightSegments.FirstAsync()).TicketingDeadline);

        // Con bloque + valor -> actualiza (normalizado a medianoche Kind=Utc).
        await service.UpdateFlightAsync(1, 1, Build(DeadlineValue, true), CancellationToken.None);
        var flight = await context.FlightSegments.FirstAsync();
        Assert.Equal(DeadlineValue.Date, flight.TicketingDeadline!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, flight.TicketingDeadline.Value.Kind);
    }

    [Fact]
    public async Task UpdateFlight_DeadlinesSpecifiedWithNull_ClearsDeadline()
    {
        // Cierra el cuadrante asimetrico que faltaba: Aereo con bloque + NULL borra (el otro test cubre
        // sin-bloque preserva + bloque+valor actualiza).
        await using var context = CreateContext();
        var (_, supplier) = await SeedAsync(context);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 1, ReservaId = 1, SupplierId = 1, AirlineCode = "AR", FlightNumber = "100",
            Origin = "EZE", Destination = "MIA",
            DepartureTime = DateTime.SpecifyKind(new DateTime(2026, 9, 1, 10, 0, 0), DateTimeKind.Utc),
            ArrivalTime = DateTime.SpecifyKind(new DateTime(2026, 9, 1, 18, 0, 0), DateTimeKind.Utc),
            SalePrice = 200m, NetCost = 100m, Status = "HK", TicketingDeadline = PersistedDeadline
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        var req = new UpdateFlightRequest(
            SupplierId: supplier.PublicId.ToString(), AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "100",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "MIA", DestinationCity: "Miami",
            DepartureTime: new DateTime(2026, 9, 1, 10, 0, 0), ArrivalTime: new DateTime(2026, 9, 1, 18, 0, 0),
            CabinClass: "Economy", Baggage: "23kg", TicketNumber: null, PNR: "ABC123",
            NetCost: 100m, SalePrice: 200m, Commission: 90m, Tax: 10m, Status: "HK", Notes: null,
            TicketingDeadline: null, DeadlinesSpecified: true);

        await service.UpdateFlightAsync(1, 1, req, CancellationToken.None);

        Assert.Null((await context.FlightSegments.FirstAsync()).TicketingDeadline);
    }
}
