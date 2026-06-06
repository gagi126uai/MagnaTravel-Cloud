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

public class BookingServiceTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new AppDbContext(options);
    }

    private static IMapper CreateMapper()
    {
        return new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();
    }

    private static BookingService CreateService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(service => service.UpdateBalanceAsync(It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService
            .Setup(service => service.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // B1.15 Fase 0.2: el masking de costos en POST/PUT de hotel es fail-closed
        // (sin HttpContext + sin resolver -> enmascara). Para preservar el contrato
        // de estos tests legacy (assert NetCost real), inyectamos un Admin con
        // accessor + resolver vacios — Admin bypass garantiza que se vea el costo.
        var adminAccessor = BuildHttpContextAccessor("admin-test", "Admin");
        var resolver = BuildResolver("admin-test"); // sin permisos (Admin no los necesita)

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
            resolver,
            adminAccessor);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(set);
        return mock.Object;
    }

    [Fact]
    public async Task CreateHotelAsync_WithRateId_UsesSubmittedManualPrices()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Hotel Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0001", Name = "Reserva test" };
        var rate = new Rate
        {
            Id = 1,
            SupplierId = supplier.Id,
            ServiceType = "Hotel",
            ProductName = "Hotel tarifario",
            HotelName = "Hotel tarifario",
            City = "Bariloche",
            RoomType = "Doble",
            MealPlan = "Desayuno",
            NetCost = 100m,
            SalePrice = 150m,
            Commission = 50m,
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new CreateHotelRequest(
            supplier.PublicId.ToString(),
            "Hotel elegido",
            4,
            "Bariloche",
            "Argentina",
            DateTime.UtcNow.Date.AddDays(10),
            DateTime.UtcNow.Date.AddDays(13),
            "Doble",
            "Desayuno",
            2,
            0,
            1,
            null,
            300m,
            777m,
            477m,
            null,
            null,
            rate.PublicId.ToString(),
            "Solicitado");

        var created = await service.CreateHotelAsync(reserva.Id, request, CancellationToken.None);

        Assert.Equal(300m, created.NetCost);
        Assert.Equal(777m, created.SalePrice);
        var storedHotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(rate.Id, storedHotel.RateId);
        Assert.Equal(777m, storedHotel.SalePrice);
    }

    [Fact]
    public async Task UpdateHotelAsync_WithExistingRateId_KeepsSubmittedManualSalePrice()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Hotel Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0002", Name = "Reserva test" };
        var rate = new Rate
        {
            Id = 1,
            SupplierId = supplier.Id,
            ServiceType = "Hotel",
            ProductName = "Hotel tarifario",
            HotelName = "Hotel tarifario",
            City = "Mendoza",
            RoomType = "Doble",
            MealPlan = "Desayuno",
            NetCost = 100m,
            SalePrice = 150m,
            Commission = 50m,
        };
        var hotel = new HotelBooking
        {
            Id = 1,
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            RateId = rate.Id,
            HotelName = "Hotel tarifario",
            City = "Mendoza",
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
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.Rates.Add(rate);
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new UpdateHotelRequest(
            supplier.PublicId.ToString(),
            "Hotel tarifario",
            4,
            "Mendoza",
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
            rate.PublicId.ToString(),
            "Solicitado");

        var updated = await service.UpdateHotelAsync(reserva.Id, hotel.Id, request, CancellationToken.None);

        Assert.Equal(240m, updated.NetCost);
        Assert.Equal(888m, updated.SalePrice);
        var storedHotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(rate.Id, storedHotel.RateId);
        Assert.Equal(888m, storedHotel.SalePrice);
    }

    // === Direccion del hotel (campo "Mas detalles", metadato inocuo) ===
    // El front recolecta la direccion pero antes el request no la tenia, asi que se descartaba.
    // Ahora se mapea por nombre a HotelBooking.Address (igual que Country/Notes: sin discriminador,
    // el modal la reenvia en cada edicion).

    [Fact]
    public async Task CreateHotelAsync_WithAddress_PersistsAddress()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Hotel Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0020", Name = "Reserva test" };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        // Carga manual (sin RateId). Address va al final como parametro opcional; usamos `with`
        // para no depender de la posicion exacta del resto de los opcionales.
        var request = new CreateHotelRequest(
            supplier.PublicId.ToString(),
            "Hotel manual",
            3,
            "Cordoba",
            "Argentina",
            DateTime.UtcNow.Date.AddDays(5),
            DateTime.UtcNow.Date.AddDays(7),
            "Doble",
            "Desayuno",
            2,
            0,
            1,
            null,
            200m,
            300m,
            100m,
            null) with { Address = "Av. Colon 1234" };

        await service.CreateHotelAsync(reserva.Id, request, CancellationToken.None);

        var storedHotel = await context.HotelBookings.SingleAsync();
        Assert.Equal("Av. Colon 1234", storedHotel.Address);
    }

    [Fact]
    public async Task UpdateHotelAsync_WithAddress_PersistsAddress()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Hotel Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0021", Name = "Reserva test" };
        var hotel = new HotelBooking
        {
            Id = 1,
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel manual",
            City = "Mendoza",
            Address = "Direccion vieja 1",
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
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new UpdateHotelRequest(
            supplier.PublicId.ToString(),
            "Hotel manual",
            3,
            "Mendoza",
            "Argentina",
            hotel.CheckIn,
            hotel.CheckOut,
            "Doble",
            "Desayuno",
            2,
            0,
            1,
            null,
            200m,
            300m,
            100m,
            "Solicitado",
            null) with { Address = "Direccion nueva 99" };

        await service.UpdateHotelAsync(reserva.Id, hotel.Id, request, CancellationToken.None);

        var storedHotel = await context.HotelBookings.SingleAsync();
        Assert.Equal("Direccion nueva 99", storedHotel.Address);
    }

    // === Trazabilidad de moneda (metadato, no afecta saldo) ===
    // Al crear un servicio desde una tarifa, copiamos rate.Currency al booking para
    // dejar registro de en que moneda se cotizo. Si no hay tarifa, queda en null.

    [Fact]
    public async Task CreateHotelAsync_WithRate_CopiesRateCurrencyForTraceability()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Hotel Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0010", Name = "Reserva test" };
        var rate = new Rate
        {
            Id = 1,
            SupplierId = supplier.Id,
            ServiceType = "Hotel",
            ProductName = "Hotel tarifario",
            HotelName = "Hotel tarifario",
            City = "Bariloche",
            RoomType = "Doble",
            MealPlan = "Desayuno",
            NetCost = 100m,
            SalePrice = 150m,
            Commission = 50m,
            Currency = "USD",
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new CreateHotelRequest(
            supplier.PublicId.ToString(),
            "Hotel elegido",
            4,
            "Bariloche",
            "Argentina",
            DateTime.UtcNow.Date.AddDays(10),
            DateTime.UtcNow.Date.AddDays(13),
            "Doble",
            "Desayuno",
            2,
            0,
            1,
            null,
            300m,
            777m,
            477m,
            null,
            null,
            rate.PublicId.ToString(),
            "Solicitado");

        var created = await service.CreateHotelAsync(reserva.Id, request, CancellationToken.None);

        // La moneda viaja al DTO y a la entidad persistida.
        Assert.Equal("USD", created.Currency);
        var storedHotel = await context.HotelBookings.SingleAsync();
        Assert.Equal("USD", storedHotel.Currency);
        // No tocamos los precios: el snapshot de precios sigue igual que antes.
        Assert.Equal(777m, storedHotel.SalePrice);
    }

    [Fact]
    public async Task CreateHotelAsync_WithoutRate_LeavesCurrencyNull()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Hotel Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0011", Name = "Reserva test" };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        // Sin RateId: es carga manual, no hay tarifa que defina la moneda.
        var request = new CreateHotelRequest(
            supplier.PublicId.ToString(),
            "Hotel manual",
            3,
            "Cordoba",
            "Argentina",
            DateTime.UtcNow.Date.AddDays(5),
            DateTime.UtcNow.Date.AddDays(7),
            "Doble",
            "Desayuno",
            2,
            0,
            1,
            null,
            200m,
            300m,
            100m,
            null,
            null,
            null,
            "Solicitado");

        var created = await service.CreateHotelAsync(reserva.Id, request, CancellationToken.None);

        // No inventamos moneda: null = legacy / no informado.
        Assert.Null(created.Currency);
        var storedHotel = await context.HotelBookings.SingleAsync();
        Assert.Null(storedHotel.Currency);
    }

    [Fact]
    public async Task CreateTransferAsync_WithRate_CopiesRateCurrencyForTraceability()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Transfer Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0012", Name = "Reserva test" };
        var rate = new Rate
        {
            Id = 1,
            SupplierId = supplier.Id,
            ServiceType = "Traslado",
            ProductName = "Traslado tarifario",
            NetCost = 50m,
            SalePrice = 80m,
            Commission = 30m,
            Currency = "USD",
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new CreateTransferRequest(
            supplier.PublicId.ToString(),
            "Aeropuerto",
            "Hotel centro",
            DateTime.UtcNow.Date.AddDays(10),
            null,
            "Sedan",
            2,
            false,
            null,
            50m,
            80m,
            30m,
            null,
            rate.PublicId.ToString(),
            "Solicitado",
            null);

        var created = await service.CreateTransferAsync(reserva.Id, request, CancellationToken.None);

        Assert.Equal("USD", created.Currency);
        var storedTransfer = await context.TransferBookings.SingleAsync();
        Assert.Equal("USD", storedTransfer.Currency);
        Assert.Equal(80m, storedTransfer.SalePrice);
    }

    // === Bloque 2: campos nuevos de Vuelo (confirmacion + pasajeros del segmento) ===
    // Verifican el viaje completo request -> entidad -> DTO de los 2 campos que se
    // sumaron a FlightSegment, sin romper el snapshot de precios ni el masking.

    [Fact]
    public async Task CreateFlightAsync_PersistsConfirmationNumberAndPassengerCount()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0020", Name = "Reserva test" };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "AR",
            AirlineName: "Aerolineas Argentinas",
            FlightNumber: "1234",
            Origin: "EZE",
            OriginCity: "Buenos Aires",
            Destination: "MIA",
            DestinationCity: "Miami",
            DepartureTime: DateTime.UtcNow.Date.AddDays(10),
            ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(9),
            CabinClass: "Economy",
            Baggage: "23kg",
            PNR: "ABC123",
            NetCost: 500m,
            SalePrice: 800m,
            Commission: 300m,
            Tax: 120m,
            Notes: null,
            RateId: null,
            WorkflowStatus: "Solicitado",
            ConfirmationNumber: "CONF-9988",
            PassengerCount: 3);

        var created = await service.CreateFlightAsync(reserva.Id, request, CancellationToken.None);

        // El DTO de salida expone los campos nuevos.
        Assert.Equal("CONF-9988", created.ConfirmationNumber);
        Assert.Equal(3, created.PassengerCount);
        // Y quedan persistidos en la entidad.
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal("CONF-9988", stored.ConfirmationNumber);
        Assert.Equal(3, stored.PassengerCount);
    }

    [Fact]
    public async Task CreateFlightAsync_PersistsTicketNumber()
    {
        // B2: antes CreateFlightRequest no tenia TicketNumber, asi que el ticket que mandaba
        // el front en el ALTA se descartaba silenciosamente (solo se guardaba en la edicion).
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0030", Name = "Reserva test" };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "AR",
            AirlineName: "Aerolineas Argentinas",
            FlightNumber: "1234",
            Origin: "EZE",
            OriginCity: "Buenos Aires",
            Destination: "MIA",
            DestinationCity: "Miami",
            DepartureTime: DateTime.UtcNow.Date.AddDays(10),
            ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(9),
            CabinClass: "Economy",
            Baggage: "23kg",
            PNR: "ABC123",
            NetCost: 500m,
            SalePrice: 800m,
            Commission: 300m,
            Tax: 120m,
            Notes: null,
            RateId: null,
            WorkflowStatus: "Solicitado",
            ConfirmationNumber: null,
            PassengerCount: null,
            TicketNumber: "045-1234567890");

        var created = await service.CreateFlightAsync(reserva.Id, request, CancellationToken.None);

        // El DTO de salida expone el ticket...
        Assert.Equal("045-1234567890", created.TicketNumber);
        // ...y queda persistido en la entidad (antes se perdia).
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal("045-1234567890", stored.TicketNumber);
    }

    [Fact]
    public async Task CreateFlightAsync_KeepsWallClockTimesWithoutTimezoneShift()
    {
        // B1: la hora de vuelo es hora local del aeropuerto (la del ticket), no un instante UTC.
        // Debe guardarse SIN corrimiento: 14:30 cargado -> 14:30 guardado, y con Kind=Utc para
        // que Npgsql la acepte en la columna timestamptz (en prod; aca usamos InMemory).
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0031", Name = "Reserva test" };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);

        // El front (corregido) manda la hora SIN "Z": llega como Kind=Unspecified.
        var departureWallClock = new DateTime(2026, 6, 15, 14, 30, 0, DateTimeKind.Unspecified);
        var arrivalWallClock = new DateTime(2026, 6, 15, 23, 45, 0, DateTimeKind.Unspecified);

        var request = new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "AR",
            AirlineName: "Aerolineas Argentinas",
            FlightNumber: "1234",
            Origin: "EZE",
            OriginCity: "Buenos Aires",
            Destination: "MIA",
            DestinationCity: "Miami",
            DepartureTime: departureWallClock,
            ArrivalTime: arrivalWallClock,
            CabinClass: "Economy",
            Baggage: "23kg",
            PNR: "ABC123",
            NetCost: 500m,
            SalePrice: 800m,
            Commission: 300m,
            Tax: 120m,
            Notes: null);

        await service.CreateFlightAsync(reserva.Id, request, CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        // La hora de pared no se movio: 14:30 sigue siendo 14:30 (no se convirtio a 17:30).
        Assert.Equal(new DateTime(2026, 6, 15, 14, 30, 0), stored.DepartureTime);
        Assert.Equal(new DateTime(2026, 6, 15, 23, 45, 0), stored.ArrivalTime);
        // Y queda marcada como Utc para que la columna timestamptz la acepte en Postgres.
        Assert.Equal(DateTimeKind.Utc, stored.DepartureTime.Kind);
        Assert.Equal(DateTimeKind.Utc, stored.ArrivalTime.Kind);
    }

    [Fact]
    public async Task CreateFlightAsync_WithoutNewFields_LeavesThemNull()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0021", Name = "Reserva test" };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        // Llamada "legacy": no manda ConfirmationNumber ni PassengerCount.
        var request = new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "LA",
            AirlineName: null,
            FlightNumber: "900",
            Origin: "EZE",
            OriginCity: null,
            Destination: "SCL",
            DestinationCity: null,
            DepartureTime: DateTime.UtcNow.Date.AddDays(5),
            ArrivalTime: DateTime.UtcNow.Date.AddDays(5).AddHours(2),
            CabinClass: "Economy",
            Baggage: null,
            PNR: null,
            NetCost: 100m,
            SalePrice: 200m,
            Commission: 100m,
            Tax: 0m,
            Notes: null);

        var created = await service.CreateFlightAsync(reserva.Id, request, CancellationToken.None);

        // No inventamos valores: campos no informados quedan en null.
        Assert.Null(created.ConfirmationNumber);
        Assert.Null(created.PassengerCount);
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Null(stored.ConfirmationNumber);
        Assert.Null(stored.PassengerCount);
    }

    [Fact]
    public async Task UpdateFlightAsync_UpdatesConfirmationNumberAndPassengerCount()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0022", Name = "Reserva test" };
        var flight = new FlightSegment
        {
            Id = 1,
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            AirlineCode = "AR",
            FlightNumber = "1234",
            Origin = "EZE",
            Destination = "MIA",
            DepartureTime = DateTime.UtcNow.Date.AddDays(10),
            ArrivalTime = DateTime.UtcNow.Date.AddDays(10).AddHours(9),
            CabinClass = "Economy",
            Status = "HK",
            NetCost = 500m,
            SalePrice = 800m,
            Commission = 300m,
            Tax = 120m,
            // Arranca sin estos campos para verificar que la edicion los completa.
            ConfirmationNumber = null,
            PassengerCount = null,
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.FlightSegments.Add(flight);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new UpdateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "AR",
            AirlineName: "Aerolineas Argentinas",
            FlightNumber: "1234",
            Origin: "EZE",
            OriginCity: "Buenos Aires",
            Destination: "MIA",
            DestinationCity: "Miami",
            DepartureTime: flight.DepartureTime,
            ArrivalTime: flight.ArrivalTime,
            CabinClass: "Business",
            Baggage: "2PC",
            TicketNumber: "044-1234567890",
            PNR: "ABC123",
            NetCost: 500m,
            SalePrice: 800m,
            Commission: 300m,
            Tax: 120m,
            Status: "HK",
            Notes: null,
            RateId: null,
            WorkflowStatus: "Confirmado",
            ConfirmationNumber: "CONF-7777",
            PassengerCount: 2);

        var updated = await service.UpdateFlightAsync(reserva.Id, flight.Id, request, CancellationToken.None);

        Assert.Equal("CONF-7777", updated.ConfirmationNumber);
        Assert.Equal(2, updated.PassengerCount);
        Assert.Equal("044-1234567890", updated.TicketNumber);
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal("CONF-7777", stored.ConfirmationNumber);
        Assert.Equal(2, stored.PassengerCount);
    }

    [Fact]
    public void PassengerDtoMapping_IncludesEditableContactFields()
    {
        var mapper = CreateMapper();
        var passenger = new Passenger
        {
            PublicId = Guid.NewGuid(),
            FullName = "Ada Lovelace",
            DocumentType = "DNI",
            DocumentNumber = "123",
            Phone = "+5491112345678",
            Email = "ada@example.com",
            Gender = "F",
            Notes = "Vegetariana",
        };

        var dto = mapper.Map<PassengerDto>(passenger);

        Assert.Equal(passenger.Phone, dto.Phone);
        Assert.Equal(passenger.Email, dto.Email);
        Assert.Equal(passenger.Gender, dto.Gender);
        Assert.Equal(passenger.Notes, dto.Notes);
    }
}
