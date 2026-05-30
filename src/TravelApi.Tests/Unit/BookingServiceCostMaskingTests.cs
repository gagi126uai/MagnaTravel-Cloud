using System.Collections.Generic;
using System.Linq;
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
/// B1.15 seguridad: regresion del leak de NetCost (lo que le cuesta a la agencia)
/// en los servicios de reserva. Antes los Create/Update/GET de Flight/Package/Transfer
/// devolvian el costo del proveedor a cualquier usuario, aun sin el permiso
/// <c>cobranzas.see_cost</c>. Estos tests fijan el contrato:
///   - usuario SIN cobranzas.see_cost -> NetCost enmascarado a 0;
///   - usuario CON cobranzas.see_cost -> NetCost real (no debe haber regresion).
///
/// El enmascarado vive en la capa de servicio (CostMasking), por eso se puede
/// testear con InMemory + Moq. El RequireOwnership de los controllers es del
/// pipeline de auth y queda para QA/integration (ver nota al pie).
/// </summary>
public class BookingServiceCostMaskingTests
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

    // Construye el servicio con un caller no-Admin. Si "canSeeCost" es true, el
    // resolver devuelve el permiso cobranzas.see_cost; si es false, devuelve vacio.
    private static BookingService CreateServiceForUser(AppDbContext context, IMapper mapper, bool canSeeCost)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
        // Importante: NO le damos rol "Admin" (Admin haria bypass del masking).
        var accessor = BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, SeeCostPermission)
            : BuildResolver(userId); // sin permisos

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
            accessor);
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
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    // Seed minimo: una reserva + un proveedor. Devuelve los ids para usar en los requests.
    private static async Task<(Reserva reserva, Supplier supplier)> SeedReservaAndSupplierAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Proveedor test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9001", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    // ============================= FLIGHT =============================

    private static CreateFlightRequest BuildCreateFlight(string supplierPublicId, decimal netCost) => new(
        SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
        Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
        DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
        CabinClass: "Economy", Baggage: null, PNR: null,
        NetCost: netCost, SalePrice: 500m, Commission: 50m, Tax: 0m, Notes: null);

    [Fact]
    public async Task CreateFlightAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var service = CreateServiceForUser(context, mapper, canSeeCost: false);

        var created = await service.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString(), 300m), CancellationToken.None);

        Assert.Equal(0m, created.NetCost); // costo oculto
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(300m, stored.NetCost); // pero NO se altera lo persistido
    }

    [Fact]
    public async Task CreateFlightAsync_UserWithSeeCost_KeepsNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var service = CreateServiceForUser(context, mapper, canSeeCost: true);

        var created = await service.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString(), 300m), CancellationToken.None);

        Assert.Equal(300m, created.NetCost);
    }

    [Fact]
    public async Task GetFlightByIdAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString(), 300m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        // Es el primer vuelo insertado: Id == 1 (usamos el overload por int interno).
        var read = await noCost.GetFlightByIdAsync(reserva.Id, 1, CancellationToken.None);

        Assert.Equal(0m, read.NetCost);
    }

    [Fact]
    public async Task GetFlightsAsync_UserWithoutSeeCost_MasksNetCostInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString(), 300m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetFlightsAsync(reserva.Id, CancellationToken.None)).ToList();

        Assert.All(list, dto => Assert.Equal(0m, dto.NetCost));
    }

    // ============================= PACKAGE =============================

    private static CreatePackageRequest BuildCreatePackage(string supplierPublicId, decimal netCost) => new(
        SupplierId: supplierPublicId, PackageName: "Paquete Caribe", Destination: "Cancun",
        StartDate: DateTime.UtcNow.Date.AddDays(20), EndDate: DateTime.UtcNow.Date.AddDays(27),
        IncludesHotel: true, IncludesFlight: true, IncludesTransfer: true, IncludesExcursions: false, IncludesMeals: true,
        Adults: 2, Children: 0, Itinerary: null,
        NetCost: netCost, SalePrice: 1500m, Commission: 150m, Notes: null);

    [Fact]
    public async Task CreatePackageAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var service = CreateServiceForUser(context, mapper, canSeeCost: false);

        var created = await service.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString(), 900m), CancellationToken.None);

        Assert.Equal(0m, created.NetCost);
        var stored = await context.PackageBookings.SingleAsync();
        Assert.Equal(900m, stored.NetCost);
    }

    [Fact]
    public async Task CreatePackageAsync_UserWithSeeCost_KeepsNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var service = CreateServiceForUser(context, mapper, canSeeCost: true);

        var created = await service.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString(), 900m), CancellationToken.None);

        Assert.Equal(900m, created.NetCost);
    }

    [Fact]
    public async Task GetPackageByIdAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString(), 900m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var read = await noCost.GetPackageByIdAsync(reserva.Id, 1, CancellationToken.None);

        Assert.Equal(0m, read.NetCost);
    }

    [Fact]
    public async Task GetPackagesAsync_UserWithoutSeeCost_MasksNetCostInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString(), 900m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetPackagesAsync(reserva.Id, CancellationToken.None)).ToList();

        Assert.All(list, dto => Assert.Equal(0m, dto.NetCost));
    }

    // ============================= TRANSFER =============================

    private static CreateTransferRequest BuildCreateTransfer(string supplierPublicId, decimal netCost) => new(
        SupplierId: supplierPublicId, PickupLocation: "Aeropuerto", DropoffLocation: "Hotel centro",
        PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Sedan", Passengers: 2,
        IsRoundTrip: false, ReturnDateTime: null,
        NetCost: netCost, SalePrice: 120m, Commission: 20m, Notes: null);

    [Fact]
    public async Task CreateTransferAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var service = CreateServiceForUser(context, mapper, canSeeCost: false);

        var created = await service.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString(), 70m), CancellationToken.None);

        Assert.Equal(0m, created.NetCost);
        var stored = await context.TransferBookings.SingleAsync();
        Assert.Equal(70m, stored.NetCost);
    }

    [Fact]
    public async Task CreateTransferAsync_UserWithSeeCost_KeepsNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var service = CreateServiceForUser(context, mapper, canSeeCost: true);

        var created = await service.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString(), 70m), CancellationToken.None);

        Assert.Equal(70m, created.NetCost);
    }

    [Fact]
    public async Task GetTransferByIdAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString(), 70m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var read = await noCost.GetTransferByIdAsync(reserva.Id, 1, CancellationToken.None);

        Assert.Equal(0m, read.NetCost);
    }

    [Fact]
    public async Task GetTransfersAsync_UserWithoutSeeCost_MasksNetCostInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString(), 70m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetTransfersAsync(reserva.Id, CancellationToken.None)).ToList();

        Assert.All(list, dto => Assert.Equal(0m, dto.NetCost));
    }

    // ============================= HOTEL (GET list + byId) =============================
    // Hotel ya enmascaraba en Create/Update; cerramos el leak que faltaba en los GET.

    private static CreateHotelRequest BuildCreateHotel(string supplierPublicId, decimal netCost) => new(
        SupplierId: supplierPublicId, HotelName: "Hotel test", StarRating: 4, City: "Bariloche", Country: "Argentina",
        CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
        RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
        NetCost: netCost, SalePrice: 400m, Commission: 100m, Notes: null);

    [Fact]
    public async Task GetHotelsAsync_UserWithoutSeeCost_MasksNetCostInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 250m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetHotelsAsync(reserva.Id, CancellationToken.None)).ToList();

        Assert.All(list, dto => Assert.Equal(0m, dto.NetCost));
    }

    [Fact]
    public async Task GetHotelByIdAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 250m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var read = await noCost.GetHotelByIdAsync(reserva.Id, 1, CancellationToken.None);

        Assert.Equal(0m, read.NetCost);
        // Sanity: el caller con see_cost si ve el costo (no hay regresion).
        var readSeeing = await seeAll.GetHotelByIdAsync(reserva.Id, 1, CancellationToken.None);
        Assert.Equal(250m, readSeeing.NetCost);
    }

    // ===================== PATCH /status (UpdateXxxStatusAsync) =====================
    // Cierra el gap detectado por el review: estos endpoints devolvian el DTO con
    // NetCost SIN enmascarar. Aca verificamos que el status-update respeta el mismo
    // contrato que Create/Update/GET: sin permiso -> 0; con permiso -> costo real.

    [Fact]
    public async Task UpdateHotelStatusAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);

        // Sembramos el hotel con un caller que SI ve costos (para que el seed quede limpio).
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 250m), CancellationToken.None);

        // Ahora un vendedor SIN permiso cambia el status: el return NO debe filtrar el costo.
        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdateHotelStatusAsync(created.PublicId.ToString(), "Confirmado", confirmationNumber: null, CancellationToken.None);

        Assert.Equal(0m, updated.NetCost);
        var stored = await context.HotelBookings.SingleAsync();
        Assert.Equal(250m, stored.NetCost); // lo persistido no se toca
    }

    [Fact]
    public async Task UpdateHotelStatusAsync_UserWithSeeCost_KeepsNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);

        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 250m), CancellationToken.None);

        var updated = await seeAll.UpdateHotelStatusAsync(created.PublicId.ToString(), "Confirmado", confirmationNumber: null, CancellationToken.None);

        Assert.Equal(250m, updated.NetCost); // sin regresion para quien tiene el permiso
    }

    [Fact]
    public async Task UpdateFlightStatusAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);

        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeAll.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString(), 300m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdateFlightStatusAsync(created.PublicId.ToString(), "Confirmado", confirmationNumber: null, CancellationToken.None);

        Assert.Equal(0m, updated.NetCost);
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(300m, stored.NetCost);
    }

    [Fact]
    public async Task UpdatePackageStatusAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);

        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeAll.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString(), 900m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdatePackageStatusAsync(created.PublicId.ToString(), "Confirmado", confirmationNumber: null, CancellationToken.None);

        Assert.Equal(0m, updated.NetCost);
        var stored = await context.PackageBookings.SingleAsync();
        Assert.Equal(900m, stored.NetCost);
    }

    [Fact]
    public async Task UpdateTransferStatusAsync_UserWithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);

        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeAll.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString(), 70m), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdateTransferStatusAsync(created.PublicId.ToString(), "Confirmado", confirmationNumber: null, CancellationToken.None);

        Assert.Equal(0m, updated.NetCost);
        var stored = await context.TransferBookings.SingleAsync();
        Assert.Equal(70m, stored.NetCost);
    }
}
