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
/// Obra "PDF completo" (2026-08-13/14), fix del bloqueante de review: los horarios de vuelo
/// (<see cref="FlightSegment.OutboundDepartureTime"/>/<c>OutboundArrivalTime</c>/<c>ReturnDepartureTime</c>/
/// <c>ReturnArrivalTime</c>) y el plan de cuotas del hotel (<see cref="HotelBooking.InstallmentsCount"/>/
/// <c>InstallmentAmount</c>) YA NO son anti-clobber: se mapean por convención, igual que Origin/Destino.
/// <para>
/// Antes esta suite se llamaba "AntiClobber" y probaba que un PUT sin estos campos CONSERVABA lo cargado.
/// Eso resultó ser el bug: la ficha inline (único emisor real de este UPDATE, ver
/// <c>ServiceInlineCard.jsx</c>) SIEMPRE manda los 6 casilleros, así que un campo vaciado en el formulario
/// ("" -&gt; null) nunca podía borrarse — quedaba pegado para siempre. El caso que importa ahora es el
/// contrario: el vendedor CARGA un horario/plan de cuotas y después lo BORRA a propósito vaciando el
/// casillero; el segundo PUT (con null) tiene que dejar el campo en null en la base.
/// </para>
/// </summary>
public class PdfCompletoFieldsClearRoundTripTests
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

    /// <summary>Mismo harness que FlightSegmentGeographicScopeTests: BookingService con InMemory + Admin bypass (no es esto lo que se prueba acá).</summary>
    private static BookingService CreateService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "admin-test"), new(ClaimTypes.Role, "Admin") };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        var resolverMock = new Mock<IUserPermissionResolver>();
        resolverMock.Setup(r => r.GetPermissionsAsync("admin-test", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());

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
            accessor);
    }

    // ================================================================================
    // FlightSegment: OutboundDepartureTime / OutboundArrivalTime / ReturnDepartureTime / ReturnArrivalTime
    // ================================================================================

    private static UpdateFlightRequest BuildFlightUpdateRequest(
        FlightSegment flight,
        Supplier supplier,
        TimeOnly? outboundDeparture,
        TimeOnly? outboundArrival,
        TimeOnly? returnDeparture,
        TimeOnly? returnArrival) => new(
        SupplierId: supplier.PublicId.ToString(),
        AirlineCode: "AR", AirlineName: "Aerolineas Argentinas", FlightNumber: "1234",
        Origin: "AEP", OriginCity: "Buenos Aires", Destination: "IGR", DestinationCity: "Iguazu",
        DepartureTime: flight.DepartureTime, ArrivalTime: flight.ArrivalTime,
        CabinClass: "Business", Baggage: "2PC", TicketNumber: null, PNR: "ABC123",
        NetCost: 500m, SalePrice: 800m, Commission: 300m, Tax: 120m,
        Status: "HK", Notes: null, RateId: null, WorkflowStatus: "Confirmado",
        OutboundDepartureTime: outboundDeparture, OutboundArrivalTime: outboundArrival,
        ReturnDepartureTime: returnDeparture, ReturnArrivalTime: returnArrival);

    [Fact]
    public async Task UpdateFlightAsync_LoadThenClearArrivalAndDepartureTimes_LeavesFieldsNull()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0200", Name = "Reserva test" };
        var flight = new FlightSegment
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id,
            AirlineCode = "AR", FlightNumber = "1234", Origin = "AEP", Destination = "IGR",
            DepartureTime = DateTime.UtcNow.Date.AddDays(10),
            ArrivalTime = DateTime.UtcNow.Date.AddDays(15),
            CabinClass = "Economy", Status = "HK",
            NetCost = 500m, SalePrice = 800m, Commission = 300m, Tax = 120m,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.FlightSegments.Add(flight);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);

        // PUT #1: el vendedor carga los 4 horarios desde la ficha inline.
        var requestWithValues = BuildFlightUpdateRequest(
            flight, supplier,
            outboundDeparture: new TimeOnly(9, 0), outboundArrival: new TimeOnly(11, 45),
            returnDeparture: new TimeOnly(19, 30), returnArrival: new TimeOnly(21, 20));
        await service.UpdateFlightAsync(reserva.Id, flight.Id, requestWithValues, CancellationToken.None);

        var storedAfterLoad = await context.FlightSegments.AsNoTracking().SingleAsync();
        Assert.Equal(new TimeOnly(9, 0), storedAfterLoad.OutboundDepartureTime);
        Assert.Equal(new TimeOnly(11, 45), storedAfterLoad.OutboundArrivalTime);
        Assert.Equal(new TimeOnly(19, 30), storedAfterLoad.ReturnDepartureTime);
        Assert.Equal(new TimeOnly(21, 20), storedAfterLoad.ReturnArrivalTime);

        // PUT #2: el vendedor vacía los 4 casilleros en la ficha inline -- el formulario manda null.
        // Esto es lo que antes NO funcionaba (anti-clobber roto): el valor quedaba pegado para siempre.
        var requestCleared = BuildFlightUpdateRequest(
            flight, supplier,
            outboundDeparture: null, outboundArrival: null, returnDeparture: null, returnArrival: null);
        var updated = await service.UpdateFlightAsync(reserva.Id, flight.Id, requestCleared, CancellationToken.None);

        Assert.Null(updated.OutboundDepartureTime);
        Assert.Null(updated.OutboundArrivalTime);
        Assert.Null(updated.ReturnDepartureTime);
        Assert.Null(updated.ReturnArrivalTime);

        var stored = await context.FlightSegments.AsNoTracking().SingleAsync();
        Assert.Null(stored.OutboundDepartureTime);
        Assert.Null(stored.OutboundArrivalTime);
        Assert.Null(stored.ReturnDepartureTime);
        Assert.Null(stored.ReturnArrivalTime);
    }

    // ================================================================================
    // HotelBooking.InstallmentsCount / InstallmentAmount
    // ================================================================================

    private static UpdateHotelRequest BuildHotelUpdateRequest(
        HotelBooking hotel, Supplier supplier, int? installmentsCount, decimal? installmentAmount) => new(
        SupplierId: supplier.PublicId.ToString(), HotelName: hotel.HotelName, StarRating: hotel.StarRating,
        City: hotel.City, Country: null, CheckIn: hotel.CheckIn, CheckOut: hotel.CheckOut,
        RoomType: hotel.RoomType, MealPlan: hotel.MealPlan,
        Adults: hotel.Adults, Children: hotel.Children, Rooms: hotel.Rooms, ConfirmationNumber: null,
        NetCost: 600m, SalePrice: 1000m, Commission: 400m, Status: "Solicitado", Notes: null,
        WorkflowStatus: "Confirmado",
        InstallmentsCount: installmentsCount, InstallmentAmount: installmentAmount);

    [Fact]
    public async Task UpdateHotelAsync_LoadThenClearInstallments_LeavesFieldsNull()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Hotelera Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0201", Name = "Reserva test" };
        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id,
            HotelName = "Hotel Palace Madrid", City = "Madrid", RoomType = "Doble", MealPlan = "Desayuno",
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(17),
            Adults = 2, Children = 0, Rooms = 1, Status = "Solicitado",
            NetCost = 600m, SalePrice = 1000m, Commission = 400m,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);

        // PUT #1: el vendedor carga el plan de cuotas ("6 CUOTAS 300 USD") desde la ficha inline.
        var requestWithValues = BuildHotelUpdateRequest(hotel, supplier, installmentsCount: 6, installmentAmount: 300m);
        await service.UpdateHotelAsync(reserva.Id, hotel.Id, requestWithValues, CancellationToken.None);

        var storedAfterLoad = await context.HotelBookings.AsNoTracking().SingleAsync();
        Assert.Equal(6, storedAfterLoad.InstallmentsCount);
        Assert.Equal(300m, storedAfterLoad.InstallmentAmount);

        // PUT #2: el vendedor vacía el plan de cuotas -- el formulario manda null. Esto es lo que antes
        // NO funcionaba: el plan quedaba pegado y el PDF seguía imprimiendo una línea de cuotas vieja.
        var requestCleared = BuildHotelUpdateRequest(hotel, supplier, installmentsCount: null, installmentAmount: null);
        var updated = await service.UpdateHotelAsync(reserva.Id, hotel.Id, requestCleared, CancellationToken.None);

        Assert.Null(updated.InstallmentsCount);
        Assert.Null(updated.InstallmentAmount);

        var stored = await context.HotelBookings.AsNoTracking().SingleAsync();
        Assert.Null(stored.InstallmentsCount);
        Assert.Null(stored.InstallmentAmount);
    }
}
