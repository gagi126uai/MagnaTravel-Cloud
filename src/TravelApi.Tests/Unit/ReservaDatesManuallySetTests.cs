using System.Collections.Generic;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Reservations;
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
/// FIX (2026-07-23, causa raiz confirmada con logs de PROD): antes, corregir a mano las fechas de la
/// reserva (ReservaService.UpdateDatesAsync) se perdia apenas se guardaba CUALQUIER servicio (hotel,
/// vuelo, transfer, paquete, asistencia) — BookingService.RecalculateReservationScheduleAsync corria
/// despues de cada alta/edicion y volvia a pisar StartDate/EndDate con el MIN/MAX automatico de los
/// servicios, sin enterarse de que alguien las habia corregido a proposito.
///
/// <para>Estos tests fijan el comportamiento nuevo: <see cref="Reserva.DatesManuallySet"/> en true hace
/// que el recalculo automatico NO toque las fechas; en false (el default de siempre) el recalculo sigue
/// funcionando exactamente igual que antes.</para>
/// </summary>
public class ReservaDatesManuallySetTests
{
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

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static BookingService CreateBookingService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permissions = new HashSet<string> { Permissions.CobranzasSeeCost };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions);

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
            accessor);
    }

    private static ReservaService CreateReservaService(AppDbContext context, IMapper mapper)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        return new ReservaService(context, mapper, settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static CreateHotelRequest BuildHotelRequest(Supplier supplier, DateTime checkIn, DateTime checkOut) => new(
        SupplierId: supplier.PublicId.ToString(),
        HotelName: "Hotel Test",
        StarRating: 4,
        City: "Bariloche",
        Country: "Argentina",
        CheckIn: checkIn,
        CheckOut: checkOut,
        RoomType: "Doble",
        MealPlan: "Desayuno",
        Adults: 2,
        Children: 0,
        Rooms: 1,
        ConfirmationNumber: null,
        NetCost: 500m,
        SalePrice: 800m,
        Commission: 300m,
        Notes: null);

    private static CreateFlightRequest BuildFlightRequest(Supplier supplier, DateTime departure) => new(
        SupplierId: supplier.PublicId.ToString(),
        AirlineCode: "AR",
        AirlineName: "Aerolineas Argentinas",
        FlightNumber: "1234",
        Origin: "AEP",
        OriginCity: "Buenos Aires",
        Destination: "BRC",
        DestinationCity: "Bariloche",
        DepartureTime: departure,
        ArrivalTime: departure.AddHours(2),
        CabinClass: "Economy",
        Baggage: null,
        PNR: null,
        NetCost: 300m,
        SalePrice: 500m,
        Commission: 200m,
        Tax: 0m,
        Notes: null);

    /// <summary>
    /// REGRESION (comportamiento de SIEMPRE, no debe romperse): reserva SIN correccion manual — el
    /// recalculo automatico sigue funcionando igual que antes del fix al guardar servicios.
    /// </summary>
    [Fact]
    public async Task SinCorreccionManual_GuardarHotel_RecalculaCabeceraComoSiempre()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-DMS-1", Name = "Reserva sin correccion", Status = EstadoReserva.InManagement };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var checkIn = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var checkOut = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
        var bookingService = CreateBookingService(context, mapper);
        await bookingService.CreateHotelAsync(reserva.Id, BuildHotelRequest(supplier, checkIn, checkOut), CancellationToken.None);

        var reloaded = await context.Reservas.SingleAsync();
        Assert.Equal(checkIn, reloaded.StartDate);
        Assert.Equal(checkOut, reloaded.EndDate);
        Assert.False(reloaded.DatesManuallySet);
    }

    /// <summary>
    /// CASO CENTRAL del fix: corregir las fechas a mano (UpdateDatesAsync) y despues guardar un HOTEL —
    /// las fechas manuales tienen que sobrevivir, aunque el hotel tenga fechas distintas.
    /// </summary>
    [Fact]
    public async Task CorreccionManual_LuegoGuardarHotel_LasFechasManualesSeMantienen()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva { Id = 1, PublicId = Guid.NewGuid(), NumeroReserva = "F-DMS-2", Name = "Reserva con correccion", Status = EstadoReserva.InManagement };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        // 1) El usuario corrige las fechas A MANO desde la ficha.
        var reservaService = CreateReservaService(context, mapper);
        var manualStart = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var manualEnd = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        await reservaService.UpdateDatesAsync(
            reserva.Id.ToString(),
            new UpdateReservaDatesRequest(StartDate: manualStart, EndDate: manualEnd));

        var afterManualFix = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.True(afterManualFix.DatesManuallySet);
        Assert.Equal(manualStart, afterManualFix.StartDate);
        Assert.Equal(manualEnd, afterManualFix.EndDate);

        // 2) Se guarda un hotel con fechas DISTINTAS a las corregidas a mano.
        var bookingService = CreateBookingService(context, mapper);
        var hotelCheckIn = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var hotelCheckOut = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        await bookingService.CreateHotelAsync(reserva.Id, BuildHotelRequest(supplier, hotelCheckIn, hotelCheckOut), CancellationToken.None);

        // ASSERT CLAVE: las fechas de la CABECERA siguen siendo las corregidas a mano, NO las del hotel.
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.Equal(manualStart, reloaded.StartDate);
        Assert.Equal(manualEnd, reloaded.EndDate);
        Assert.True(reloaded.DatesManuallySet);
    }

    /// <summary>
    /// Mismo caso central, con un SEGUNDO tipo de servicio (vuelo) para probar que el guard aplica
    /// parejo a los 5 tipos (todos pasan por el mismo RecalculateReservationScheduleAsync privado).
    /// </summary>
    [Fact]
    public async Task CorreccionManual_LuegoGuardarVuelo_LasFechasManualesSeMantienen()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Test" };
        var reserva = new Reserva { Id = 1, PublicId = Guid.NewGuid(), NumeroReserva = "F-DMS-3", Name = "Reserva con correccion vuelo", Status = EstadoReserva.InManagement };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var reservaService = CreateReservaService(context, mapper);
        var manualStart = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var manualEnd = new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc);
        await reservaService.UpdateDatesAsync(
            reserva.Id.ToString(),
            new UpdateReservaDatesRequest(StartDate: manualStart, EndDate: manualEnd));

        var bookingService = CreateBookingService(context, mapper);
        var flightDeparture = new DateTime(2027, 1, 1, 10, 0, 0, DateTimeKind.Utc); // fuera del rango manual
        await bookingService.CreateFlightAsync(reserva.Id, BuildFlightRequest(supplier, flightDeparture), CancellationToken.None);

        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.Equal(manualStart, reloaded.StartDate);
        Assert.Equal(manualEnd, reloaded.EndDate);
    }
}
