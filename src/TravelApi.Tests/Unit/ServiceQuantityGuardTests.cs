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
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// QA con navegador contra PROD (2026-08-11): se guardo un servicio de hotel con Habitaciones = -1 y
/// el backend lo acepto. La ficha de carga ahora lo frena en pantalla, pero el freno de verdad tiene
/// que estar en el motor (T-3): estos tests barren los 5 tipos de servicio (hotel, aereo, traslado,
/// paquete, asistencia) en el ALTA y en la EDICION, y fijan los textos exactos que ve el vendedor
/// (T-6) para que no se rompan sin querer en un refactor.
/// </summary>
public class ServiceQuantityGuardTests
{
    private static readonly DateTime Inicio = DateTime.SpecifyKind(new DateTime(2026, 9, 10, 10, 0, 0), DateTimeKind.Utc);
    private static readonly DateTime Fin = Inicio.AddDays(4);

    private const string TextoHabitaciones = "Las habitaciones tienen que ser al menos 1.";
    private const string TextoPasajeros = "Los pasajeros tienen que ser al menos 1.";

    // =====================================================================
    // Los textos, sueltos (la regla pura, sin base de datos de por medio)
    // =====================================================================

    [Fact]
    public void ServiceQuantityRules_FixesTheExactTextsTheUserReads()
    {
        Assert.Equal(TextoHabitaciones, ServiceQuantityRules.RoomsBelowMinimumMessage);
        Assert.Equal(TextoPasajeros, ServiceQuantityRules.PassengersBelowMinimumMessage);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void EnsureRoomsAtLeastOne_RejectsZeroOrNegative(int habitaciones)
    {
        var error = Assert.Throws<ServiceQuantityValidationException>(
            () => ServiceQuantityRules.EnsureRoomsAtLeastOne(habitaciones));
        Assert.Equal(TextoHabitaciones, error.Message);
    }

    [Fact]
    public void EnsurePassengersAtLeastOne_AcceptsZeroChildren_ButNotAnEmptyService()
    {
        // Dos adultos y ningun menor es lo normal: no se rechaza.
        ServiceQuantityRules.EnsurePassengersAtLeastOne(adults: 2, children: 0);

        // Nadie viajando, o un casillero en negativo aunque el total de positivo: se rechaza.
        Assert.Throws<ServiceQuantityValidationException>(
            () => ServiceQuantityRules.EnsurePassengersAtLeastOne(adults: 0, children: 0));
        Assert.Throws<ServiceQuantityValidationException>(
            () => ServiceQuantityRules.EnsurePassengersAtLeastOne(adults: 3, children: -1));
    }

    [Fact]
    public void EnsurePassengersAtLeastOneWhenInformed_AllowsNotInformed()
    {
        // El aereo puede quedar "sin informar" (null): eso NO es un dato roto.
        ServiceQuantityRules.EnsurePassengersAtLeastOneWhenInformed(null);

        Assert.Throws<ServiceQuantityValidationException>(
            () => ServiceQuantityRules.EnsurePassengersAtLeastOneWhenInformed(0));
    }

    // =====================================================================
    // Armado del servicio real (mismo molde que ServiceDateOrderValidationTests)
    // =====================================================================

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

    private static BookingService CreateBookingService(AppDbContext context)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permissions = new HashSet<string> { Permissions.CobranzasSeeCost };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions);

        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

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
            CreateMapper(),
            NullLogger<BookingService>.Instance,
            resolver.Object,
            accessor,
            settings.Object);
    }

    private static async Task<(Reserva reserva, Supplier supplier)> SeedAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-CANT", Name = "Reserva cantidades" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    // =====================================================================
    // HOTEL — el caso que se encontro en PROD
    // =====================================================================

    private static CreateHotelRequest BuildCreateHotel(string supplierPublicId, int rooms, int adults, int children)
        => new(
            SupplierId: supplierPublicId, HotelName: "Hotel Test", StarRating: 4, City: "Bariloche", Country: "Argentina",
            CheckIn: Inicio, CheckOut: Fin, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: adults, Children: children, Rooms: rooms, ConfirmationNumber: null,
            NetCost: 600m, SalePrice: 1000m, Commission: 400m, Notes: null, Currency: "ARS");

    [Fact]
    public async Task CreateHotel_WithNegativeRooms_IsRejected_AndNothingIsSaved()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), rooms: -1, adults: 2, children: 0), CancellationToken.None));

        Assert.Equal(TextoHabitaciones, error.Message);
        Assert.Equal(0, await context.HotelBookings.CountAsync());
    }

    [Fact]
    public async Task CreateHotel_WithoutAnyPassenger_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), rooms: 1, adults: 0, children: 0), CancellationToken.None));

        Assert.Equal(TextoPasajeros, error.Message);
        Assert.Equal(0, await context.HotelBookings.CountAsync());
    }

    [Fact]
    public async Task UpdateHotel_WithNegativeRooms_IsRejected_AndTheSavedRoomsAreNotTouched()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 70, ReservaId = reserva.Id, SupplierId = supplier.Id,
            HotelName = "Hotel Test", City = "Bariloche", RoomType = "Doble", MealPlan = "Desayuno",
            CheckIn = Inicio, CheckOut = Fin, Adults = 2, Children = 0, Rooms = 2,
            Status = "Solicitado", SalePrice = 1000m
        });
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var request = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel Test", StarRating: 4, City: "Bariloche",
            Country: "Argentina", CheckIn: Inicio, CheckOut: Fin, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: -1, ConfirmationNumber: null,
            NetCost: 600m, SalePrice: 1000m, Commission: 400m, Status: "Solicitado", Notes: null);

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.UpdateHotelAsync(reserva.Id, 70, request, CancellationToken.None));

        Assert.Equal(TextoHabitaciones, error.Message);
        Assert.Equal(2, (await context.HotelBookings.AsNoTracking().SingleAsync()).Rooms);
    }

    // =====================================================================
    // AEREO — la cantidad es opcional, pero si viene tiene que ser real
    // =====================================================================

    private static CreateFlightRequest BuildCreateFlight(string supplierPublicId, int? passengerCount)
        => new(
            SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: Inicio, ArrivalTime: Fin, CabinClass: null, Baggage: null, PNR: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Tax: 0m, Notes: null,
            PassengerCount: passengerCount, Currency: "ARS");

    [Fact]
    public async Task CreateFlight_WithZeroPassengers_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString(), passengerCount: 0), CancellationToken.None));

        Assert.Equal(TextoPasajeros, error.Message);
        Assert.Equal(0, await context.FlightSegments.CountAsync());
    }

    [Fact]
    public async Task CreateFlight_WithPassengersNotInformed_IsStillAllowed()
    {
        // Anti-sobre-freno: "sin informar" es un dato valido de siempre en el aereo.
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        await service.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString(), passengerCount: null), CancellationToken.None);

        Assert.Equal(1, await context.FlightSegments.CountAsync());
    }

    [Fact]
    public async Task UpdateFlight_WithNegativePassengers_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 71, ReservaId = reserva.Id, SupplierId = supplier.Id,
            AirlineCode = "AR", FlightNumber = "1234", Origin = "EZE", Destination = "BRC",
            DepartureTime = Inicio, ArrivalTime = Fin, PassengerCount = 3,
            Status = "Solicitado", SalePrice = 500m
        });
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var request = new UpdateFlightRequest(
            SupplierId: supplier.PublicId.ToString(), AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: Inicio, ArrivalTime: Fin, CabinClass: null, Baggage: null,
            TicketNumber: null, PNR: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Tax: 0m, Status: "Solicitado", Notes: null,
            PassengerCount: -2);

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.UpdateFlightAsync(reserva.Id, 71, request, CancellationToken.None));

        Assert.Equal(TextoPasajeros, error.Message);
        Assert.Equal(3, (await context.FlightSegments.AsNoTracking().SingleAsync()).PassengerCount);
    }

    // =====================================================================
    // TRASLADO
    // =====================================================================

    [Fact]
    public async Task CreateTransfer_WithZeroPassengers_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        var request = new CreateTransferRequest(
            SupplierId: supplier.PublicId.ToString(), PickupLocation: "Aeropuerto EZE", DropoffLocation: "Hotel Centro",
            PickupDateTime: Inicio, FlightNumber: null, VehicleType: null, Passengers: 0,
            IsRoundTrip: false, ReturnDateTime: null,
            NetCost: 50m, SalePrice: 80m, Commission: 30m, Notes: null, Currency: "ARS");

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.CreateTransferAsync(reserva.Id, request, CancellationToken.None));

        Assert.Equal(TextoPasajeros, error.Message);
        Assert.Equal(0, await context.TransferBookings.CountAsync());
    }

    [Fact]
    public async Task UpdateTransfer_WithNegativePassengers_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.TransferBookings.Add(new TransferBooking
        {
            Id = 72, ReservaId = reserva.Id, SupplierId = supplier.Id,
            PickupLocation = "Aeropuerto EZE", DropoffLocation = "Hotel Centro",
            PickupDateTime = Inicio, Passengers = 2, Status = "Solicitado", SalePrice = 80m
        });
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var request = new UpdateTransferRequest(
            SupplierId: supplier.PublicId.ToString(), PickupLocation: "Aeropuerto EZE", DropoffLocation: "Hotel Centro",
            PickupDateTime: Inicio, FlightNumber: null, VehicleType: null, Passengers: -1,
            IsRoundTrip: false, ReturnDateTime: null, ConfirmationNumber: null,
            NetCost: 50m, SalePrice: 80m, Commission: 30m, Status: "Solicitado", Notes: null);

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.UpdateTransferAsync(reserva.Id, 72, request, CancellationToken.None));

        Assert.Equal(TextoPasajeros, error.Message);
        Assert.Equal(2, (await context.TransferBookings.AsNoTracking().SingleAsync()).Passengers);
    }

    // =====================================================================
    // PAQUETE
    // =====================================================================

    [Fact]
    public async Task CreatePackage_WithoutAnyPassenger_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        var request = new CreatePackageRequest(
            SupplierId: supplier.PublicId.ToString(), PackageName: "Caribe Total", Destination: "Cancun",
            StartDate: Inicio, EndDate: Fin,
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 0, Children: 0, Itinerary: null,
            NetCost: 800m, SalePrice: 1000m, Commission: 200m, Notes: null, Currency: "ARS");

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.CreatePackageAsync(reserva.Id, request, CancellationToken.None));

        Assert.Equal(TextoPasajeros, error.Message);
        Assert.Equal(0, await context.PackageBookings.CountAsync());
    }

    [Fact]
    public async Task UpdatePackage_WithNegativeChildren_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 73, ReservaId = reserva.Id, SupplierId = supplier.Id,
            PackageName = "Caribe Total", StartDate = Inicio, EndDate = Fin,
            Adults = 2, Children = 1, Status = "Solicitado", SalePrice = 1000m
        });
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var request = new UpdatePackageRequest(
            SupplierId: supplier.PublicId.ToString(), PackageName: "Caribe Total", Destination: "Cancun",
            StartDate: Inicio, EndDate: Fin,
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 3, Children: -1, Itinerary: null, ConfirmationNumber: null,
            NetCost: 800m, SalePrice: 1000m, Commission: 200m, Status: "Solicitado", Notes: null);

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.UpdatePackageAsync(reserva.Id, 73, request, CancellationToken.None));

        Assert.Equal(TextoPasajeros, error.Message);
        Assert.Equal(1, (await context.PackageBookings.AsNoTracking().SingleAsync()).Children);
    }

    // =====================================================================
    // ASISTENCIA
    // =====================================================================

    [Fact]
    public async Task CreateAssistance_WithoutAnyPassenger_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        var request = new CreateAssistanceRequest(
            SupplierId: supplier.PublicId.ToString(),
            ValidFrom: Inicio, ValidTo: Fin,
            Adults: 0, Children: 0,
            NetCost: 40m, SalePrice: 70m, Commission: 30m, Currency: "ARS");

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.CreateAssistanceAsync(reserva.Id, request, CancellationToken.None));

        Assert.Equal(TextoPasajeros, error.Message);
        Assert.Equal(0, await context.AssistanceBookings.CountAsync());
    }

    [Fact]
    public async Task UpdateAssistance_WithoutAnyPassenger_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.AssistanceBookings.Add(new AssistanceBooking
        {
            Id = 74, ReservaId = reserva.Id, SupplierId = supplier.Id,
            ValidFrom = Inicio, ValidTo = Fin, Adults = 2, Children = 0,
            Status = "Solicitado", SalePrice = 70m
        });
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var request = new UpdateAssistanceRequest(
            SupplierId: supplier.PublicId.ToString(),
            ValidFrom: Inicio, ValidTo: Fin,
            Adults: 0, Children: 0,
            NetCost: 40m, SalePrice: 70m, Commission: 30m, Status: "Solicitado");

        var error = await Assert.ThrowsAsync<ServiceQuantityValidationException>(() =>
            service.UpdateAssistanceAsync(reserva.Id, 74, request, CancellationToken.None));

        Assert.Equal(TextoPasajeros, error.Message);
        Assert.Equal(2, (await context.AssistanceBookings.AsNoTracking().SingleAsync()).Adults);
    }
}
