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
/// Bug en vivo 2026-06-06: la ficha inline manda fechas peladas ("2026-08-12") que el binder JSON
/// deserializa con Kind=Unspecified, y Npgsql (sin EnableLegacyTimestampBehavior) las RECHAZA con
/// DbUpdateException al escribir columnas 'timestamp with time zone'. InMemory NO valida el Kind
/// (por eso ningun test lo atrapo), pero SI nos deja assertar el Kind persistido: estos tests pinean
/// el contrato "toda fecha que llega de un request se persiste con Kind=Utc" en los 5 tipos de
/// servicio y en los 3 paths de escritura (create legacy flag OFF, create catalogo flag ON, update).
///
/// <para>Criterios del sistema (ver BookingService):
/// - fechas date-only (CheckIn/CheckOut, StartDate/EndDate, ValidFrom/ValidTo): NormalizeCalendarDate
///   (medianoche + Kind=Utc, "fecha de pared disfrazada de Utc");
/// - horas de pared (DepartureTime/ArrivalTime, PickupDateTime/ReturnDateTime): NormalizeAirportWallClock
///   (hora local tal cual + Kind=Utc, sin corrimiento).</para>
/// </summary>
public class BookingServiceDateKindNormalizationTests
{
    // Fecha pelada como la deja el binder JSON con "2026-08-12": Kind=Unspecified, hora 00:00.
    private static readonly DateTime BareStart = new(2026, 8, 12);
    private static readonly DateTime BareEnd = new(2026, 8, 15);
    // Hora de pared como la deja el binder con "2026-08-12T14:30:00" (sin Z): Kind=Unspecified.
    private static readonly DateTime BareWallClock = new(2026, 8, 12, 14, 30, 0);

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

    private static BookingService CreateService(AppDbContext context, IMapper mapper, bool catalogFlagOn)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Caller CON ver-costos para que "request manda" y el test no dependa del masking.
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
            .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = catalogFlagOn });

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
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-KIND", Name = "Reserva kind" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    private static void AssertCalendarDateUtc(DateTime stored, DateTime expectedDate)
    {
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        // La fecha calendario que eligio el vendedor NO se corre; queda a medianoche.
        Assert.Equal(expectedDate.Date, stored.Date);
        Assert.Equal(TimeSpan.Zero, stored.TimeOfDay);
    }

    private static void AssertWallClockUtc(DateTime stored, DateTime expectedWallClock)
    {
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        // La hora de pared se preserva tal cual (14:30 sigue siendo 14:30, sin conversion).
        Assert.Equal(expectedWallClock, stored);
    }

    // ===================== HOTEL (CheckIn/CheckOut date-only) =====================

    private static CreateHotelRequest BuildCreateHotelRequest(string supplierPublicId, string? currency = null)
        => new(
            SupplierId: supplierPublicId, HotelName: "Hotel Maitei", StarRating: 4, City: "Posadas", Country: "Argentina",
            CheckIn: BareStart, CheckOut: BareEnd, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 200m, SalePrice: 300m, Commission: 100m, Notes: null,
            Currency: currency);

    [Fact]
    public async Task CreateHotel_LegacyPath_UnspecifiedDates_PersistsKindUtc()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        await service.CreateHotelAsync(reserva.Id, BuildCreateHotelRequest(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        AssertCalendarDateUtc(stored.CheckIn, BareStart);
        AssertCalendarDateUtc(stored.CheckOut, BareEnd);
    }

    [Fact]
    public async Task CreateHotel_CatalogPath_UnspecifiedDates_PersistsKindUtc()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: true);

        // Path catalogo "alta manual" (sin RateId ni producto nuevo): Currency obligatoria con flag ON.
        await service.CreateHotelAsync(reserva.Id, BuildCreateHotelRequest(supplier.PublicId.ToString(), currency: "ARS"), CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        AssertCalendarDateUtc(stored.CheckIn, BareStart);
        AssertCalendarDateUtc(stored.CheckOut, BareEnd);
    }

    [Fact]
    public async Task UpdateHotel_UnspecifiedDates_PersistsKindUtc()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = reserva.Id, SupplierId = supplier.Id, HotelName = "Hotel Maitei", City = "Posadas",
            CheckIn = DateTime.UtcNow.Date, CheckOut = DateTime.UtcNow.Date.AddDays(1),
            RoomType = "Doble", MealPlan = "Desayuno", Status = "Solicitado", SalePrice = 300m
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        var request = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel Maitei", StarRating: 4, City: "Posadas", Country: "Argentina",
            CheckIn: BareStart, CheckOut: BareEnd, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 200m, SalePrice: 300m, Commission: 100m, Status: "Solicitado", Notes: null);
        await service.UpdateHotelAsync(reserva.Id, 10, request, CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync(h => h.Id == 10);
        AssertCalendarDateUtc(stored.CheckIn, BareStart);
        AssertCalendarDateUtc(stored.CheckOut, BareEnd);
    }

    // ===================== PACKAGE (StartDate/EndDate date-only) =====================

    private static CreatePackageRequest BuildCreatePackageRequest(string supplierPublicId, string? currency = null)
        => new(
            SupplierId: supplierPublicId, PackageName: "Caribe Total", Destination: "Cancun",
            StartDate: BareStart, EndDate: BareEnd,
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null,
            NetCost: 800m, SalePrice: 1000m, Commission: 200m, Notes: null,
            Currency: currency);

    [Fact]
    public async Task CreatePackage_LegacyPath_UnspecifiedDates_PersistsKindUtc()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        await service.CreatePackageAsync(reserva.Id, BuildCreatePackageRequest(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        AssertCalendarDateUtc(stored.StartDate, BareStart);
        Assert.NotNull(stored.EndDate);
        AssertCalendarDateUtc(stored.EndDate!.Value, BareEnd);
    }

    [Fact]
    public async Task CreatePackage_CatalogPath_UnspecifiedDates_PersistsKindUtc()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: true);

        await service.CreatePackageAsync(reserva.Id, BuildCreatePackageRequest(supplier.PublicId.ToString(), currency: "ARS"), CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        AssertCalendarDateUtc(stored.StartDate, BareStart);
        Assert.NotNull(stored.EndDate);
        AssertCalendarDateUtc(stored.EndDate!.Value, BareEnd);
    }

    [Fact]
    public async Task UpdatePackage_UnspecifiedStart_NullEnd_PersistsKindUtcAndNull()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 20, ReservaId = reserva.Id, SupplierId = supplier.Id, PackageName = "Caribe Total", Destination = "Cancun",
            StartDate = DateTime.UtcNow.Date, EndDate = DateTime.UtcNow.Date.AddDays(7),
            Status = "Solicitado", SalePrice = 1000m
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        // EndDate null = la ficha permite omitir la fecha de fin (ADR-018): debe persistir null sin explotar.
        var request = new UpdatePackageRequest(
            SupplierId: supplier.PublicId.ToString(), PackageName: "Caribe Total", Destination: "Cancun",
            StartDate: BareStart, EndDate: null,
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null, ConfirmationNumber: null,
            NetCost: 800m, SalePrice: 1000m, Commission: 200m, Status: "Solicitado", Notes: null);
        await service.UpdatePackageAsync(reserva.Id, 20, request, CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync(p => p.Id == 20);
        AssertCalendarDateUtc(stored.StartDate, BareStart);
        Assert.Null(stored.EndDate);
    }

    // ===================== ASSISTANCE (ValidFrom/ValidTo date-only) =====================

    private static CreateAssistanceRequest BuildCreateAssistanceRequest(string supplierPublicId, string? currency = null)
        => new(
            SupplierId: supplierPublicId,
            ValidFrom: BareStart, ValidTo: BareEnd,
            Adults: 2, Children: 0,
            NetCost: 100m, SalePrice: 150m, Commission: 50m,
            Currency: currency);

    [Fact]
    public async Task CreateAssistance_LegacyPath_UnspecifiedDates_PersistsKindUtc()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        await service.CreateAssistanceAsync(reserva.Id, BuildCreateAssistanceRequest(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        AssertCalendarDateUtc(stored.ValidFrom, BareStart);
        AssertCalendarDateUtc(stored.ValidTo, BareEnd);
    }

    [Fact]
    public async Task CreateAssistance_CatalogPath_UnspecifiedDates_PersistsKindUtc()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: true);

        await service.CreateAssistanceAsync(reserva.Id, BuildCreateAssistanceRequest(supplier.PublicId.ToString(), currency: "ARS"), CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        AssertCalendarDateUtc(stored.ValidFrom, BareStart);
        AssertCalendarDateUtc(stored.ValidTo, BareEnd);
    }

    [Fact]
    public async Task UpdateAssistance_UnspecifiedDates_PersistsKindUtc()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.AssistanceBookings.Add(new AssistanceBooking
        {
            Id = 30, ReservaId = reserva.Id, SupplierId = supplier.Id,
            ValidFrom = DateTime.UtcNow.Date, ValidTo = DateTime.UtcNow.Date.AddDays(10),
            Adults = 2, Children = 0, Status = "Solicitado", SalePrice = 150m
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        var request = new UpdateAssistanceRequest(
            SupplierId: supplier.PublicId.ToString(),
            ValidFrom: BareStart, ValidTo: BareEnd,
            Adults: 2, Children: 0,
            NetCost: 100m, SalePrice: 150m, Commission: 50m,
            Status: "Solicitado");
        await service.UpdateAssistanceAsync(reserva.Id, 30, request, CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync(a => a.Id == 30);
        AssertCalendarDateUtc(stored.ValidFrom, BareStart);
        AssertCalendarDateUtc(stored.ValidTo, BareEnd);
    }

    // ===================== FLIGHT (DepartureTime/ArrivalTime hora de pared) =====================
    // Estos paths YA normalizaban (NormalizeAirportWallClock); los tests pinean el contrato para
    // que una regresion futura no reintroduzca el Kind=Unspecified.

    private static CreateFlightRequest BuildCreateFlightRequest(string supplierPublicId, string? currency = null)
        => new(
            SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: BareWallClock, ArrivalTime: BareWallClock.AddHours(2), CabinClass: null, Baggage: null, PNR: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Tax: 0m, Notes: null,
            Currency: currency);

    [Fact]
    public async Task CreateFlight_LegacyPath_UnspecifiedTimes_PersistsKindUtcWallClock()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        await service.CreateFlightAsync(reserva.Id, BuildCreateFlightRequest(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        AssertWallClockUtc(stored.DepartureTime, DateTime.SpecifyKind(BareWallClock, DateTimeKind.Utc));
        AssertWallClockUtc(stored.ArrivalTime, DateTime.SpecifyKind(BareWallClock.AddHours(2), DateTimeKind.Utc));
    }

    [Fact]
    public async Task CreateFlight_CatalogPath_UnspecifiedTimes_PersistsKindUtcWallClock()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: true);

        await service.CreateFlightAsync(reserva.Id, BuildCreateFlightRequest(supplier.PublicId.ToString(), currency: "ARS"), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        AssertWallClockUtc(stored.DepartureTime, DateTime.SpecifyKind(BareWallClock, DateTimeKind.Utc));
        AssertWallClockUtc(stored.ArrivalTime, DateTime.SpecifyKind(BareWallClock.AddHours(2), DateTimeKind.Utc));
    }

    [Fact]
    public async Task UpdateFlight_UnspecifiedTimes_PersistsKindUtcWallClock()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 40, ReservaId = reserva.Id, SupplierId = supplier.Id,
            AirlineCode = "AR", FlightNumber = "1234", Origin = "EZE", Destination = "BRC",
            DepartureTime = DateTime.UtcNow, ArrivalTime = DateTime.UtcNow.AddHours(2),
            Status = "Solicitado", SalePrice = 500m
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        var request = new UpdateFlightRequest(
            SupplierId: supplier.PublicId.ToString(), AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: BareWallClock, ArrivalTime: BareWallClock.AddHours(2), CabinClass: null, Baggage: null,
            TicketNumber: null, PNR: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Tax: 0m, Status: "Solicitado", Notes: null);
        await service.UpdateFlightAsync(reserva.Id, 40, request, CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync(f => f.Id == 40);
        AssertWallClockUtc(stored.DepartureTime, DateTime.SpecifyKind(BareWallClock, DateTimeKind.Utc));
        AssertWallClockUtc(stored.ArrivalTime, DateTime.SpecifyKind(BareWallClock.AddHours(2), DateTimeKind.Utc));
    }

    // ===================== TRANSFER (PickupDateTime/ReturnDateTime hora de pared) =====================

    private static CreateTransferRequest BuildCreateTransferRequest(string supplierPublicId, string? currency = null)
        => new(
            SupplierId: supplierPublicId, PickupLocation: "Aeropuerto EZE", DropoffLocation: "Hotel Centro",
            PickupDateTime: BareWallClock, FlightNumber: null, VehicleType: null, Passengers: 2,
            IsRoundTrip: true, ReturnDateTime: BareWallClock.AddDays(7),
            NetCost: 50m, SalePrice: 80m, Commission: 30m, Notes: null,
            Currency: currency);

    [Fact]
    public async Task CreateTransfer_LegacyPath_UnspecifiedTimes_PersistsKindUtcWallClock()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        await service.CreateTransferAsync(reserva.Id, BuildCreateTransferRequest(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        AssertWallClockUtc(stored.PickupDateTime, DateTime.SpecifyKind(BareWallClock, DateTimeKind.Utc));
        Assert.NotNull(stored.ReturnDateTime);
        AssertWallClockUtc(stored.ReturnDateTime!.Value, DateTime.SpecifyKind(BareWallClock.AddDays(7), DateTimeKind.Utc));
    }

    [Fact]
    public async Task CreateTransfer_CatalogPath_UnspecifiedTimes_PersistsKindUtcWallClock()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateService(context, CreateMapper(), catalogFlagOn: true);

        await service.CreateTransferAsync(reserva.Id, BuildCreateTransferRequest(supplier.PublicId.ToString(), currency: "ARS"), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        AssertWallClockUtc(stored.PickupDateTime, DateTime.SpecifyKind(BareWallClock, DateTimeKind.Utc));
        Assert.NotNull(stored.ReturnDateTime);
        AssertWallClockUtc(stored.ReturnDateTime!.Value, DateTime.SpecifyKind(BareWallClock.AddDays(7), DateTimeKind.Utc));
    }

    [Fact]
    public async Task UpdateTransfer_UnspecifiedTimes_PersistsKindUtcWallClock()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.TransferBookings.Add(new TransferBooking
        {
            Id = 50, ReservaId = reserva.Id, SupplierId = supplier.Id,
            PickupLocation = "Aeropuerto EZE", DropoffLocation = "Hotel Centro",
            PickupDateTime = DateTime.UtcNow, Passengers = 2,
            Status = "Solicitado", SalePrice = 80m
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper(), catalogFlagOn: false);

        var request = new UpdateTransferRequest(
            SupplierId: supplier.PublicId.ToString(), PickupLocation: "Aeropuerto EZE", DropoffLocation: "Hotel Centro",
            PickupDateTime: BareWallClock, FlightNumber: null, VehicleType: null, Passengers: 2,
            IsRoundTrip: true, ReturnDateTime: BareWallClock.AddDays(7), ConfirmationNumber: null,
            NetCost: 50m, SalePrice: 80m, Commission: 30m, Status: "Solicitado", Notes: null);
        await service.UpdateTransferAsync(reserva.Id, 50, request, CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync(t => t.Id == 50);
        AssertWallClockUtc(stored.PickupDateTime, DateTime.SpecifyKind(BareWallClock, DateTimeKind.Utc));
        Assert.NotNull(stored.ReturnDateTime);
        AssertWallClockUtc(stored.ReturnDateTime!.Value, DateTime.SpecifyKind(BareWallClock.AddDays(7), DateTimeKind.Utc));
    }
}
