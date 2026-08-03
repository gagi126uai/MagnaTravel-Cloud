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
/// Semaforo de DNI vencido para cabotaje (fix 2026-08-03): alta/edicion del AMBITO GEOGRAFICO
/// (<see cref="ServiceGeographicScope"/>) del VUELO REAL (<see cref="FlightSegment"/>), espejo de
/// <see cref="ServicioReservaGeographicScopeTests"/> para el servicio generico.
///
/// <para>Hueco que cierra este archivo: el campo se habia agregado SOLO a <c>ServicioReserva</c> (tabla
/// generica), pero los vuelos de verdad se cargan por <c>CreateFlightRequest</c>/<c>UpdateFlightRequest</c>
/// -&gt; <see cref="FlightSegment"/>, un camino distinto donde el campo no existia y el backend lo tiraba
/// en silencio.</para>
/// </summary>
public class FlightSegmentGeographicScopeTests
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
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static BookingService CreateService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Admin bypass: mismo patron que BookingServiceTests para no chocar con el masking de costos
        // (no es lo que este archivo prueba).
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

    private static CreateFlightRequest BuildCreateRequest(Supplier supplier, string? geographicScope) => new(
        SupplierId: supplier.PublicId.ToString(),
        AirlineCode: "AR",
        AirlineName: "Aerolineas Argentinas",
        FlightNumber: "1234",
        Origin: "AEP",
        OriginCity: "Buenos Aires",
        Destination: "IGR",
        DestinationCity: "Iguazu",
        DepartureTime: DateTime.UtcNow.Date.AddDays(10),
        ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
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
        GeographicScope: geographicScope);

    [Fact]
    public async Task CreateFlightAsync_ConNacional_QuedaGuardadoComoDomestic()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0100", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var created = await service.CreateFlightAsync(reserva.Id, BuildCreateRequest(supplier, "Nacional"), CancellationToken.None);

        Assert.Equal("Nacional", created.GeographicScope);
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(ServiceGeographicScope.Domestic, stored.GeographicScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cualquier-cosa-rara")]
    public async Task CreateFlightAsync_SinAmbitoOTextoNoReconocido_QuedaSinDefinir_NuncaCortaElAlta(string? geographicScope)
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0101", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var created = await service.CreateFlightAsync(reserva.Id, BuildCreateRequest(supplier, geographicScope), CancellationToken.None);

        Assert.Null(created.GeographicScope);
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(ServiceGeographicScope.Undefined, stored.GeographicScope);
    }

    [Fact]
    public async Task UpdateFlightAsync_ConAmbitoNuevo_PisaElAnterior()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0102", Name = "Reserva test" };
        var flight = new FlightSegment
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id,
            AirlineCode = "AR", FlightNumber = "1234", Origin = "AEP", Destination = "IGR",
            DepartureTime = DateTime.UtcNow.Date.AddDays(10),
            ArrivalTime = DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass = "Economy", Status = "HK",
            NetCost = 500m, SalePrice = 800m, Commission = 300m, Tax = 120m,
            GeographicScope = ServiceGeographicScope.Domestic,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.FlightSegments.Add(flight);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new UpdateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "AR", AirlineName: "Aerolineas Argentinas", FlightNumber: "1234",
            Origin: "AEP", OriginCity: "Buenos Aires", Destination: "IGR", DestinationCity: "Iguazu",
            DepartureTime: flight.DepartureTime, ArrivalTime: flight.ArrivalTime,
            CabinClass: "Business", Baggage: "2PC", TicketNumber: null, PNR: "ABC123",
            NetCost: 500m, SalePrice: 800m, Commission: 300m, Tax: 120m,
            Status: "HK", Notes: null, RateId: null, WorkflowStatus: "Confirmado",
            GeographicScope: "Internacional");

        var updated = await service.UpdateFlightAsync(reserva.Id, flight.Id, request, CancellationToken.None);

        Assert.Equal("Internacional", updated.GeographicScope);
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(ServiceGeographicScope.International, stored.GeographicScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("texto-no-reconocido")]
    public async Task UpdateFlightAsync_SinAmbitoOTextoNoReconocido_ConservaElAmbitoCargado_NuncaLoBorra(string? geographicScope)
    {
        // Anti-pisado (mismo criterio que ProductName/los deadlines): un caller viejo que no manda
        // el ambito NO puede "volver a Sin definir" un vuelo que ya lo tenia cargado.
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0103", Name = "Reserva test" };
        var flight = new FlightSegment
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id,
            AirlineCode = "AR", FlightNumber = "1234", Origin = "AEP", Destination = "IGR",
            DepartureTime = DateTime.UtcNow.Date.AddDays(10),
            ArrivalTime = DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass = "Economy", Status = "HK",
            NetCost = 500m, SalePrice = 800m, Commission = 300m, Tax = 120m,
            GeographicScope = ServiceGeographicScope.Domestic,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.FlightSegments.Add(flight);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new UpdateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "AR", AirlineName: "Aerolineas Argentinas", FlightNumber: "1234",
            Origin: "AEP", OriginCity: "Buenos Aires", Destination: "IGR", DestinationCity: "Iguazu",
            DepartureTime: flight.DepartureTime, ArrivalTime: flight.ArrivalTime,
            CabinClass: "Business", Baggage: "2PC", TicketNumber: null, PNR: "ABC123",
            NetCost: 500m, SalePrice: 800m, Commission: 300m, Tax: 120m,
            Status: "HK", Notes: null, RateId: null, WorkflowStatus: "Confirmado",
            GeographicScope: geographicScope);

        var updated = await service.UpdateFlightAsync(reserva.Id, flight.Id, request, CancellationToken.None);

        Assert.Equal("Nacional", updated.GeographicScope);
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(ServiceGeographicScope.Domestic, stored.GeographicScope);
    }

    [Fact]
    public async Task UpdateFlightAsync_ConTokenSinDefinir_VuelveASinDefinir()
    {
        // Fix del 2026-08-03: un vuelo marcado "Nacional" por error ahora SI puede volver a
        // "Sin definir" mandando el token ServiceGeographicScopeText.Cleared. Antes de este fix,
        // el unico texto que llegaba a mandar el front para "Sin definir" era vacio/null, que
        // ParseOrNull interpreta como "no toque el campo" (anti-pisado) — el aviso de DNI quedaba
        // prendido para siempre.
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Aerolinea Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0104", Name = "Reserva test" };
        var flight = new FlightSegment
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id,
            AirlineCode = "AR", FlightNumber = "1234", Origin = "AEP", Destination = "IGR",
            DepartureTime = DateTime.UtcNow.Date.AddDays(10),
            ArrivalTime = DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass = "Economy", Status = "HK",
            NetCost = 500m, SalePrice = 800m, Commission = 300m, Tax = 120m,
            GeographicScope = ServiceGeographicScope.Domestic,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.FlightSegments.Add(flight);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new UpdateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "AR", AirlineName: "Aerolineas Argentinas", FlightNumber: "1234",
            Origin: "AEP", OriginCity: "Buenos Aires", Destination: "IGR", DestinationCity: "Iguazu",
            DepartureTime: flight.DepartureTime, ArrivalTime: flight.ArrivalTime,
            CabinClass: "Business", Baggage: "2PC", TicketNumber: null, PNR: "ABC123",
            NetCost: 500m, SalePrice: 800m, Commission: 300m, Tax: 120m,
            Status: "HK", Notes: null, RateId: null, WorkflowStatus: "Confirmado",
            GeographicScope: ServiceGeographicScopeText.Cleared);

        var updated = await service.UpdateFlightAsync(reserva.Id, flight.Id, request, CancellationToken.None);

        Assert.Null(updated.GeographicScope);
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(ServiceGeographicScope.Undefined, stored.GeographicScope);
    }

    [Fact]
    public void FlightSegmentDto_GeographicScope_ViajaComoStringLegible_NuncaElEnteroDelEnum()
    {
        var mapper = CreateMapper();

        var nacional = mapper.Map<FlightSegmentDto>(new FlightSegment { GeographicScope = ServiceGeographicScope.Domestic });
        var internacional = mapper.Map<FlightSegmentDto>(new FlightSegment { GeographicScope = ServiceGeographicScope.International });
        var sinDefinir = mapper.Map<FlightSegmentDto>(new FlightSegment { GeographicScope = ServiceGeographicScope.Undefined });

        Assert.Equal("Nacional", nacional.GeographicScope);
        Assert.Equal("Internacional", internacional.GeographicScope);
        Assert.Null(sinDefinir.GeographicScope);
    }
}
