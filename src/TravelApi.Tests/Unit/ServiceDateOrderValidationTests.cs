using System.Collections.Generic;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Identity.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Integridad de datos (2026-06-25): valida que ninguna alta/edicion de servicio acepte fechas IMPOSIBLES
/// (fin antes que inicio). Hotel y Asistencia ya lo validaban; estos tests cubren los 4 tipos que NO lo
/// hacian: Aereo (llegada vs salida), Traslado (regreso vs salida), Paquete (fin vs inicio) y el servicio
/// generico (regreso vs salida). La regla aplica SOLO cuando ambas fechas estan presentes: los campos
/// nullable (vuelo solo de ida, traslado sin regreso, paquete sin fin) siguen siendo validos.
/// </summary>
public class ServiceDateOrderValidationTests
{
    private static readonly DateTime Departure = DateTime.SpecifyKind(new DateTime(2026, 8, 12, 10, 0, 0), DateTimeKind.Utc);
    // Fecha de "fin" anterior a la de inicio: el caso imposible que debe rechazarse.
    private static readonly DateTime EndBeforeStart = Departure.AddDays(-2);
    // Fecha de "fin" valida (posterior).
    private static readonly DateTime EndAfterStart = Departure.AddDays(5);

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

    // ============================== BookingService (Aereo / Traslado / Paquete) ==============================

    private static BookingService CreateBookingService(AppDbContext context, IMapper mapper)
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

        // Flag de catalogo APAGADO: probamos el path legacy. Un test extra cubre el path catalogo.
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = false });

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
            resolver.Object,
            accessor,
            settings.Object);
    }

    private static async Task<(Reserva reserva, Supplier supplier)> SeedAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-DATES", Name = "Reserva fechas" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    // -------- AEREO --------

    private static CreateFlightRequest BuildCreateFlight(string supplierPublicId, DateTime departure, DateTime? arrival)
        => new(
            SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: departure, ArrivalTime: arrival, CabinClass: null, Baggage: null, PNR: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Tax: 0m, Notes: null, Currency: null);

    [Fact]
    public async Task CreateFlight_ArrivalBeforeDeparture_Throws()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString(), Departure, EndBeforeStart), CancellationToken.None));
        Assert.Contains("llegada", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.FlightSegments.CountAsync());
    }

    [Fact]
    public async Task CreateFlight_OneWay_NullArrival_Allowed()
    {
        // Vuelo solo de ida: ArrivalTime null es valido y NO se valida.
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context, CreateMapper());

        await service.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString(), Departure, null), CancellationToken.None);
        Assert.Equal(1, await context.FlightSegments.CountAsync());
    }

    [Fact]
    public async Task UpdateFlight_ArrivalBeforeDeparture_Throws()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 40, ReservaId = reserva.Id, SupplierId = supplier.Id,
            AirlineCode = "AR", FlightNumber = "1234", Origin = "EZE", Destination = "BRC",
            DepartureTime = Departure, ArrivalTime = EndAfterStart, Status = "Solicitado", SalePrice = 500m
        });
        await context.SaveChangesAsync();
        var service = CreateBookingService(context, CreateMapper());

        var request = new UpdateFlightRequest(
            SupplierId: supplier.PublicId.ToString(), AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: Departure, ArrivalTime: EndBeforeStart, CabinClass: null, Baggage: null,
            TicketNumber: null, PNR: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Tax: 0m, Status: "Solicitado", Notes: null);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateFlightAsync(reserva.Id, 40, request, CancellationToken.None));
        Assert.Contains("llegada", ex.Message, StringComparison.OrdinalIgnoreCase);
        // La fecha original no se piso.
        Assert.Equal(EndAfterStart, (await context.FlightSegments.AsNoTracking().SingleAsync()).ArrivalTime);
    }

    // -------- TRASLADO --------

    private static CreateTransferRequest BuildCreateTransfer(string supplierPublicId, DateTime pickup, DateTime? ret)
        => new(
            SupplierId: supplierPublicId, PickupLocation: "Aeropuerto EZE", DropoffLocation: "Hotel Centro",
            PickupDateTime: pickup, FlightNumber: null, VehicleType: null, Passengers: 2,
            IsRoundTrip: ret.HasValue, ReturnDateTime: ret,
            NetCost: 50m, SalePrice: 80m, Commission: 30m, Notes: null, Currency: null);

    [Fact]
    public async Task CreateTransfer_ReturnBeforePickup_Throws()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString(), Departure, EndBeforeStart), CancellationToken.None));
        Assert.Contains("regreso", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.TransferBookings.CountAsync());
    }

    [Fact]
    public async Task CreateTransfer_OneWay_NullReturn_Allowed()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context, CreateMapper());

        await service.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString(), Departure, null), CancellationToken.None);
        Assert.Equal(1, await context.TransferBookings.CountAsync());
    }

    [Fact]
    public async Task UpdateTransfer_ReturnBeforePickup_Throws()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.TransferBookings.Add(new TransferBooking
        {
            Id = 50, ReservaId = reserva.Id, SupplierId = supplier.Id,
            PickupLocation = "Aeropuerto EZE", DropoffLocation = "Hotel Centro",
            PickupDateTime = Departure, ReturnDateTime = EndAfterStart, Passengers = 2,
            Status = "Solicitado", SalePrice = 80m
        });
        await context.SaveChangesAsync();
        var service = CreateBookingService(context, CreateMapper());

        var request = new UpdateTransferRequest(
            SupplierId: supplier.PublicId.ToString(), PickupLocation: "Aeropuerto EZE", DropoffLocation: "Hotel Centro",
            PickupDateTime: Departure, FlightNumber: null, VehicleType: null, Passengers: 2,
            IsRoundTrip: true, ReturnDateTime: EndBeforeStart, ConfirmationNumber: null,
            NetCost: 50m, SalePrice: 80m, Commission: 30m, Status: "Solicitado", Notes: null);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateTransferAsync(reserva.Id, 50, request, CancellationToken.None));
        Assert.Contains("regreso", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------- PAQUETE --------

    private static CreatePackageRequest BuildCreatePackage(string supplierPublicId, DateTime start, DateTime? end)
        => new(
            SupplierId: supplierPublicId, PackageName: "Caribe Total", Destination: "Cancun",
            StartDate: start, EndDate: end,
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null,
            NetCost: 800m, SalePrice: 1000m, Commission: 200m, Notes: null, Currency: null);

    [Fact]
    public async Task CreatePackage_EndBeforeStart_Throws()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString(), Departure, EndBeforeStart), CancellationToken.None));
        Assert.Contains("fin", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.PackageBookings.CountAsync());
    }

    [Fact]
    public async Task CreatePackage_NullEnd_Allowed()
    {
        // La ficha "producto-primero" permite omitir la fecha de fin: null es valido y NO se valida.
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context, CreateMapper());

        await service.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString(), Departure, null), CancellationToken.None);
        Assert.Equal(1, await context.PackageBookings.CountAsync());
    }

    [Fact]
    public async Task UpdatePackage_EndBeforeStart_Throws()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 20, ReservaId = reserva.Id, SupplierId = supplier.Id, PackageName = "Caribe Total", Destination = "Cancun",
            StartDate = Departure, EndDate = EndAfterStart, Status = "Solicitado", SalePrice = 1000m
        });
        await context.SaveChangesAsync();
        var service = CreateBookingService(context, CreateMapper());

        var request = new UpdatePackageRequest(
            SupplierId: supplier.PublicId.ToString(), PackageName: "Caribe Total", Destination: "Cancun",
            StartDate: Departure, EndDate: EndBeforeStart,
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null, ConfirmationNumber: null,
            NetCost: 800m, SalePrice: 1000m, Commission: 200m, Status: "Solicitado", Notes: null);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdatePackageAsync(reserva.Id, 20, request, CancellationToken.None));
        Assert.Contains("fin", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ============================== ReservaService (servicio generico) ==============================

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService CreateReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        var mapper = new Mock<IMapper>();
        return new ReservaService(context, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static AddServiceRequest BuildAddService(DateTime departure, DateTime? ret)
        => new(
            ServiceType: "Excursion",
            DepartureDate: departure,
            ReturnDate: ret,
            SalePrice: 100m,
            NetCost: 60m,
            SupplierId: null,
            Description: "City tour",
            ConfirmationNumber: null,
            OperatorPaymentDeadline: null);

    [Fact]
    public async Task AddGenericService_ReturnBeforeDeparture_Throws()
    {
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-GEN", Name = "Generico", Status = EstadoReserva.Budget });
        await context.SaveChangesAsync();
        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddServiceAsync(1, BuildAddService(Departure, EndBeforeStart), CancellationToken.None));
        Assert.Contains("regreso", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.Servicios.CountAsync());
    }

    [Fact]
    public async Task AddGenericService_NullReturn_Allowed()
    {
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-GEN", Name = "Generico", Status = EstadoReserva.Budget });
        await context.SaveChangesAsync();
        var service = CreateReservaService(context);

        await service.AddServiceAsync(1, BuildAddService(Departure, null), CancellationToken.None);
        Assert.Equal(1, await context.Servicios.CountAsync());
    }

    [Fact]
    public async Task UpdateGenericService_ReturnBeforeDeparture_Throws()
    {
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-GEN", Name = "Generico", Status = EstadoReserva.Budget });
        context.Servicios.Add(new ServicioReserva
        {
            Id = 70, ReservaId = 1, ServiceType = "Excursion", ProductType = "Excursion",
            Description = "City tour", ConfirmationNumber = "X",
            DepartureDate = Departure, ReturnDate = EndAfterStart, Status = "Solicitado", SalePrice = 100m
        });
        await context.SaveChangesAsync();
        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateServiceAsync(70, BuildAddService(Departure, EndBeforeStart), CancellationToken.None));
        Assert.Contains("regreso", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
