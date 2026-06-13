using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
/// ADR-017 (pills de la fila del servicio, F2/F3): contrato de los DOS campos nuevos en los DTOs de los
/// 5 tipos de servicio (Hotel/Flight/Transfer/Package/Assistance):
///
///   - <c>CostToConfirm</c>: MARCA de costo (guia UX linea 81 — quien no ve costos no ve montos NI
///     marcas de costo). Sin <c>cobranzas.see_cost</c> SIEMPRE vuelve false, en todos los paths de
///     lectura: listados, byId, response de create/update/status y detalle de la reserva.
///   - <c>ProductCreatedInSale</c>: derivado de <c>Rate.CreatedInSale</c> del producto vinculado.
///     NO es dato de costo: lo ven todos (NO se enmascara), incluso quien no ve costos.
///
/// Patron de flag: los campos viajan SIEMPRE, sin gate; con EnableCatalogFindOrCreate OFF su valor
/// es neutro (false) porque nada los escribe.
/// </summary>
public class ServiceCatalogPillsDtoTests
{
    private const string SeeCostPermission = Permissions.CobranzasSeeCost;

    // ============================================================ infra compartida ============================================================

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

    // Construye el BookingService con un caller no-Admin (mismo patron que BookingServiceCostMaskingTests).
    // catalogFlagOn: con false (default) NO se inyecta settings service -> flag OFF (fail-closed, path
    // legacy, igual que los tests originales). Con true se inyecta EnableCatalogFindOrCreate=true, que es
    // requisito de los Confirm*CostAsync (con flag OFF el endpoint "no existe": FeatureNotEnabledException).
    private static BookingService CreateServiceForUser(AppDbContext context, IMapper mapper, bool canSeeCost, bool catalogFlagOn = false)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        // ADR-027: overload nuevo que pasan los paths de edicion (marca "confirmada con cambios").
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
        var accessor = BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, SeeCostPermission)
            : BuildResolver(userId);

        IOperationalFinanceSettingsService? settingsService = null;
        if (catalogFlagOn)
        {
            var settingsMock = new Mock<IOperationalFinanceSettingsService>();
            settingsMock
                .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = true });
            settingsService = settingsMock.Object;
        }

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
            accessor,
            settingsService);
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

    private static async Task<(Reserva reserva, Supplier supplier)> SeedReservaAndSupplierAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Proveedor test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9001", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    // Producto del catalogo: si createdInSale=true simula uno nacido inline en una venta (F1.3).
    // El ServiceType "Hotel" es irrelevante para estos asserts: la derivacion de ProductCreatedInSale
    // solo mira Rate.CreatedInSale del RateId vinculado, ignora el tipo.
    private static async Task<Rate> SeedRateAsync(AppDbContext context, int id, bool createdInSale)
    {
        var rate = new Rate
        {
            Id = id,
            SupplierId = 1,
            ServiceType = "Hotel",
            ProductName = $"Producto {id}",
            NetCost = 100m,
            SalePrice = 150m,
            Commission = 50m,
            CreatedInSale = createdInSale
        };
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return rate;
    }

    private static CreateHotelRequest BuildCreateHotel(string supplierPublicId) => new(
        SupplierId: supplierPublicId, HotelName: "Hotel test", StarRating: 4, City: "Bariloche", Country: "Argentina",
        CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
        RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
        NetCost: 250m, SalePrice: 400m, Commission: 150m, Notes: null);

    private static UpdateHotelRequest BuildUpdateHotel(string supplierPublicId) => new(
        SupplierId: supplierPublicId, HotelName: "Hotel test", StarRating: 4, City: "Bariloche", Country: "Argentina",
        CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
        RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
        NetCost: 250m, SalePrice: 400m, Commission: 150m, Status: "Solicitado", Notes: null);

    private static CreateFlightRequest BuildCreateFlight(string supplierPublicId) => new(
        SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
        Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
        DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
        CabinClass: "Economy", Baggage: null, PNR: null,
        NetCost: 300m, SalePrice: 500m, Commission: 200m, Tax: 0m, Notes: null);

    private static CreateTransferRequest BuildCreateTransfer(string supplierPublicId) => new(
        SupplierId: supplierPublicId, PickupLocation: "Aeropuerto", DropoffLocation: "Hotel centro",
        PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Sedan", Passengers: 2,
        IsRoundTrip: false, ReturnDateTime: null,
        NetCost: 70m, SalePrice: 120m, Commission: 50m, Notes: null);

    private static CreatePackageRequest BuildCreatePackage(string supplierPublicId) => new(
        SupplierId: supplierPublicId, PackageName: "Paquete Caribe", Destination: "Cancun",
        StartDate: DateTime.UtcNow.Date.AddDays(20), EndDate: DateTime.UtcNow.Date.AddDays(27),
        IncludesHotel: true, IncludesFlight: true, IncludesTransfer: true, IncludesExcursions: false, IncludesMeals: true,
        Adults: 2, Children: 0, Itinerary: null,
        NetCost: 900m, SalePrice: 1500m, Commission: 600m, Notes: null);

    private static CreateAssistanceRequest BuildCreateAssistance(string supplierPublicId) => new(
        SupplierId: supplierPublicId,
        ValidFrom: DateTime.UtcNow.Date.AddDays(10), ValidTo: DateTime.UtcNow.Date.AddDays(17),
        Adults: 2, Children: 0,
        NetCost: 50m, SalePrice: 90m, Commission: 40m);

    // Updates espejo de los creates: mismos valores, Status "Solicitado" (igual que BuildUpdateHotel).

    private static UpdateFlightRequest BuildUpdateFlight(string supplierPublicId) => new(
        SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
        Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
        DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
        CabinClass: "Economy", Baggage: null, TicketNumber: null, PNR: null,
        NetCost: 300m, SalePrice: 500m, Commission: 200m, Tax: 0m, Status: "Solicitado", Notes: null);

    private static UpdateTransferRequest BuildUpdateTransfer(string supplierPublicId) => new(
        SupplierId: supplierPublicId, PickupLocation: "Aeropuerto", DropoffLocation: "Hotel centro",
        PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Sedan", Passengers: 2,
        IsRoundTrip: false, ReturnDateTime: null, ConfirmationNumber: null,
        NetCost: 70m, SalePrice: 120m, Commission: 50m, Status: "Solicitado", Notes: null);

    private static UpdatePackageRequest BuildUpdatePackage(string supplierPublicId) => new(
        SupplierId: supplierPublicId, PackageName: "Paquete Caribe", Destination: "Cancun",
        StartDate: DateTime.UtcNow.Date.AddDays(20), EndDate: DateTime.UtcNow.Date.AddDays(27),
        IncludesHotel: true, IncludesFlight: true, IncludesTransfer: true, IncludesExcursions: false, IncludesMeals: true,
        Adults: 2, Children: 0, Itinerary: null, ConfirmationNumber: null,
        NetCost: 900m, SalePrice: 1500m, Commission: 600m, Status: "Solicitado", Notes: null);

    private static UpdateAssistanceRequest BuildUpdateAssistance(string supplierPublicId) => new(
        SupplierId: supplierPublicId,
        ValidFrom: DateTime.UtcNow.Date.AddDays(10), ValidTo: DateTime.UtcNow.Date.AddDays(17),
        Adults: 2, Children: 0,
        NetCost: 50m, SalePrice: 90m, Commission: 40m, Status: "Solicitado");

    // ============================================================ CostToConfirm: masking ============================================================
    // El flag OFF nunca setea la marca, asi que para probar el masking la seteamos directo en la
    // entidad (simula un servicio que quedo "a confirmar" con el flag ON).

    [Fact]
    public async Task GetHotelsAsync_UserWithoutSeeCost_MasksCostToConfirmInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetHotelsAsync(reserva.Id, CancellationToken.None)).ToList();

        // Assert.Single (no Assert.All): con lista vacia Assert.All pasa sin verificar nada.
        var dto = Assert.Single(list);
        Assert.False(dto.CostToConfirm); // marca de costo oculta
        // Lo persistido NO se altera (la mascara opera sobre el DTO).
        Assert.True((await context.HotelBookings.SingleAsync()).CostToConfirm);
    }

    [Fact]
    public async Task GetHotelsAsync_UserWithSeeCost_KeepsCostToConfirmInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var list = (await seeAll.GetHotelsAsync(reserva.Id, CancellationToken.None)).ToList();

        var dto = Assert.Single(list);
        Assert.True(dto.CostToConfirm); // sin regresion para quien ve costos
    }

    // Los loops de masking de los listados estan copy-pasteados POR TIPO en BookingService (no comparten
    // helper), asi que cubrir Hotel no cubre a los demas: un test espejo por cada uno de los 4 restantes.

    [Fact]
    public async Task GetFlightsAsync_UserWithoutSeeCost_MasksCostToConfirmInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetFlightsAsync(reserva.Id, CancellationToken.None)).ToList();

        // Assert.Single (no Assert.All): con lista vacia Assert.All pasa sin verificar nada.
        var dto = Assert.Single(list);
        Assert.False(dto.CostToConfirm); // marca de costo oculta
        // Lo persistido NO se altera (la mascara opera sobre el DTO).
        Assert.True((await context.FlightSegments.SingleAsync()).CostToConfirm);
    }

    [Fact]
    public async Task GetTransfersAsync_UserWithoutSeeCost_MasksCostToConfirmInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetTransfersAsync(reserva.Id, CancellationToken.None)).ToList();

        var dto = Assert.Single(list);
        Assert.False(dto.CostToConfirm);
        Assert.True((await context.TransferBookings.SingleAsync()).CostToConfirm);
    }

    [Fact]
    public async Task GetPackagesAsync_UserWithoutSeeCost_MasksCostToConfirmInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetPackagesAsync(reserva.Id, CancellationToken.None)).ToList();

        var dto = Assert.Single(list);
        Assert.False(dto.CostToConfirm);
        Assert.True((await context.PackageBookings.SingleAsync()).CostToConfirm);
    }

    [Fact]
    public async Task GetAssistancesAsync_UserWithoutSeeCost_MasksCostToConfirmInList()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateAssistanceAsync(reserva.Id, BuildCreateAssistance(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetAssistancesAsync(reserva.Id, CancellationToken.None)).ToList();

        var dto = Assert.Single(list);
        Assert.False(dto.CostToConfirm);
        Assert.True((await context.AssistanceBookings.SingleAsync()).CostToConfirm);
    }

    [Fact]
    public async Task GetHotelByIdAsync_CostToConfirm_MaskedWithoutPermission_KeptWithPermission()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var masked = await noCost.GetHotelByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.False(masked.CostToConfirm);

        var visible = await seeAll.GetHotelByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.True(visible.CostToConfirm);
    }

    [Fact]
    public async Task GetFlightByIdAsync_UserWithoutSeeCost_MasksCostToConfirm()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var masked = await noCost.GetFlightByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.False(masked.CostToConfirm);
    }

    [Fact]
    public async Task GetTransferByIdAsync_UserWithoutSeeCost_MasksCostToConfirm()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var masked = await noCost.GetTransferByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.False(masked.CostToConfirm);
    }

    [Fact]
    public async Task GetPackageByIdAsync_UserWithoutSeeCost_MasksCostToConfirm()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var masked = await noCost.GetPackageByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.False(masked.CostToConfirm);
    }

    [Fact]
    public async Task GetAssistanceByIdAsync_UserWithoutSeeCost_MasksCostToConfirm()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateAssistanceAsync(reserva.Id, BuildCreateAssistance(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var masked = await noCost.GetAssistanceByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.False(masked.CostToConfirm);
    }

    [Fact]
    public async Task UpdateHotelStatusAsync_UserWithoutSeeCost_MasksCostToConfirmInResponse()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdateHotelStatusAsync(created.PublicId.ToString(), "Confirmado", confirmationNumber: null, CancellationToken.None);

        Assert.False(updated.CostToConfirm); // response de mutacion tambien enmascara
        Assert.True((await context.HotelBookings.SingleAsync()).CostToConfirm); // la marca persiste
    }

    [Fact]
    public async Task UpdateHotelAsync_UserWithoutSeeCost_MasksCostToConfirmInResponse_AndDoesNotClearMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdateHotelAsync(reserva.Id, stored.Id, BuildUpdateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        Assert.False(updated.CostToConfirm); // response enmascarado
        // D8c: guardar normal NUNCA confirma — la marca queda intacta en la base.
        Assert.True((await context.HotelBookings.SingleAsync()).CostToConfirm);
    }

    // Los updates por tipo reusan cada uno SU Mask*Async: un test espejo por cada tipo no-Hotel
    // pinea el contrato (response enmascarado + guardar normal no limpia la marca, D8c).

    [Fact]
    public async Task UpdateFlightAsync_UserWithoutSeeCost_MasksCostToConfirmInResponse_AndDoesNotClearMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdateFlightAsync(reserva.Id, stored.Id, BuildUpdateFlight(supplier.PublicId.ToString()), CancellationToken.None);

        Assert.False(updated.CostToConfirm); // response enmascarado
        Assert.True((await context.FlightSegments.SingleAsync()).CostToConfirm); // la marca persiste (D8c)
    }

    [Fact]
    public async Task UpdateTransferAsync_UserWithoutSeeCost_MasksCostToConfirmInResponse_AndDoesNotClearMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdateTransferAsync(reserva.Id, stored.Id, BuildUpdateTransfer(supplier.PublicId.ToString()), CancellationToken.None);

        Assert.False(updated.CostToConfirm);
        Assert.True((await context.TransferBookings.SingleAsync()).CostToConfirm);
    }

    [Fact]
    public async Task UpdatePackageAsync_UserWithoutSeeCost_MasksCostToConfirmInResponse_AndDoesNotClearMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdatePackageAsync(reserva.Id, stored.Id, BuildUpdatePackage(supplier.PublicId.ToString()), CancellationToken.None);

        Assert.False(updated.CostToConfirm);
        Assert.True((await context.PackageBookings.SingleAsync()).CostToConfirm);
    }

    [Fact]
    public async Task UpdateAssistanceAsync_UserWithoutSeeCost_MasksCostToConfirmInResponse_AndDoesNotClearMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateAssistanceAsync(reserva.Id, BuildCreateAssistance(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        stored.CostToConfirm = true;
        await context.SaveChangesAsync();

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var updated = await noCost.UpdateAssistanceAsync(reserva.Id, stored.Id, BuildUpdateAssistance(supplier.PublicId.ToString()), CancellationToken.None);

        Assert.False(updated.CostToConfirm);
        Assert.True((await context.AssistanceBookings.SingleAsync()).CostToConfirm);
    }

    // ============================================================ ProductCreatedInSale: derivacion ============================================================

    [Fact]
    public async Task GetHotelsAsync_RateCreatedInSale_VisibleEvenWithoutSeeCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        stored.RateId = rate.Id;
        await context.SaveChangesAsync();

        // Caller SIN permiso de costos: la pill NO es dato de costo, debe verla igual.
        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var list = (await noCost.GetHotelsAsync(reserva.Id, CancellationToken.None)).ToList();

        var dto = Assert.Single(list);
        Assert.True(dto.ProductCreatedInSale); // visible para todos
        Assert.Equal(0m, dto.NetCost);          // el costo sigue enmascarado
        Assert.False(dto.CostToConfirm);        // y la marca de costo tambien
    }

    [Fact]
    public async Task GetHotelByIdAsync_ProductCreatedInSale_DerivedFromLinkedRate()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rateInSale = await SeedRateAsync(context, id: 50, createdInSale: true);
        var rateBackOffice = await SeedRateAsync(context, id: 51, createdInSale: false);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);

        // Tres hoteles: con rate creado en venta / con rate de back-office / sin rate.
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);
        var hotels = await context.HotelBookings.OrderBy(h => h.Id).ToListAsync();
        hotels[0].RateId = rateInSale.Id;
        hotels[1].RateId = rateBackOffice.Id;
        await context.SaveChangesAsync();

        Assert.True((await seeAll.GetHotelByIdAsync(reserva.Id, hotels[0].Id, CancellationToken.None)).ProductCreatedInSale);
        Assert.False((await seeAll.GetHotelByIdAsync(reserva.Id, hotels[1].Id, CancellationToken.None)).ProductCreatedInSale);
        Assert.False((await seeAll.GetHotelByIdAsync(reserva.Id, hotels[2].Id, CancellationToken.None)).ProductCreatedInSale);
    }

    [Fact]
    public async Task GetFlightByIdAsync_RateCreatedInSale_SetsProductCreatedInSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        stored.RateId = rate.Id;
        await context.SaveChangesAsync();

        // Clear: sin esto el fixup del tracker deja la nav Rate poblada y el assert seria vacuo
        // (no probaria el stamp explicito de produccion; mismo motivo que en los tests de confirm).
        context.ChangeTracker.Clear();

        var read = await seeAll.GetFlightByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.True(read.ProductCreatedInSale);
    }

    [Fact]
    public async Task GetTransferByIdAsync_RateCreatedInSale_SetsProductCreatedInSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        stored.RateId = rate.Id;
        await context.SaveChangesAsync();

        // Clear: evita la vacuidad por fixup del tracker (ver test espejo de Flight).
        context.ChangeTracker.Clear();

        var read = await seeAll.GetTransferByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.True(read.ProductCreatedInSale);
    }

    [Fact]
    public async Task GetPackageByIdAsync_RateCreatedInSale_SetsProductCreatedInSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        stored.RateId = rate.Id;
        await context.SaveChangesAsync();

        // Clear: evita la vacuidad por fixup del tracker (ver test espejo de Flight).
        context.ChangeTracker.Clear();

        var read = await seeAll.GetPackageByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.True(read.ProductCreatedInSale);
    }

    [Fact]
    public async Task GetAssistanceByIdAsync_RateCreatedInSale_SetsProductCreatedInSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateAssistanceAsync(reserva.Id, BuildCreateAssistance(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        stored.RateId = rate.Id;
        await context.SaveChangesAsync();

        // Clear: evita la vacuidad por fixup del tracker (ver test espejo de Flight).
        context.ChangeTracker.Clear();

        var read = await seeAll.GetAssistanceByIdAsync(reserva.Id, stored.Id, CancellationToken.None);
        Assert.True(read.ProductCreatedInSale);
    }

    // ============================================================ confirm-cost: stamp en el response ============================================================
    // El front reemplaza la fila con el response del confirm: sin el stamp explicito de
    // ProductCreatedInSale (la nav Rate no viene cargada en ese path), la pill violeta
    // DESAPARECERIA de la fila justo al confirmar el costo. Estos tests pinean ese contrato
    // en los 5 tipos, mas la limpieza de la marca (DTO y base).
    // Confirm exige el flag ON (con OFF el endpoint "no existe"), por eso catalogFlagOn: true.

    [Fact]
    public async Task ConfirmHotelCostAsync_ResponseKeepsProductCreatedInSale_AndClearsMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        // Simula un servicio que quedo "a confirmar" con el flag ON, vinculado a un producto nacido en venta.
        var stored = await context.HotelBookings.SingleAsync();
        stored.RateId = rate.Id;
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        // Sin esto el test es VACUO: el contexto trackeado hace relationship fixup y deja la nav Rate
        // poblada, asi que el map ya daria ProductCreatedInSale=true ANTES del stamp explicito del
        // confirm. Clear simula el contexto fresco de produccion (FindAsync sin Include).
        context.ChangeTracker.Clear();

        var confirmer = CreateServiceForUser(context, mapper, canSeeCost: true, catalogFlagOn: true);
        var dto = await confirmer.ConfirmHotelCostAsync(reserva.Id.ToString(), stored.PublicId.ToString(), new ConfirmCostRequest(), CancellationToken.None);

        Assert.True(dto.ProductCreatedInSale); // la pill violeta sobrevive al confirm
        Assert.False(dto.CostToConfirm);       // y la marca ambar se apaga en el response
        var persisted = await context.HotelBookings.AsNoTracking().SingleAsync();
        Assert.False(persisted.CostToConfirm);
        Assert.Null(persisted.CostToConfirmReason);
    }

    [Fact]
    public async Task ConfirmFlightCostAsync_ResponseKeepsProductCreatedInSale_AndClearsMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        stored.RateId = rate.Id;
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        // Sin esto el test es VACUO: el contexto trackeado hace relationship fixup y deja la nav Rate
        // poblada, asi que el map ya daria ProductCreatedInSale=true ANTES del stamp explicito del
        // confirm. Clear simula el contexto fresco de produccion (FindAsync sin Include).
        context.ChangeTracker.Clear();

        var confirmer = CreateServiceForUser(context, mapper, canSeeCost: true, catalogFlagOn: true);
        var dto = await confirmer.ConfirmFlightCostAsync(reserva.Id.ToString(), stored.PublicId.ToString(), new ConfirmCostRequest(), CancellationToken.None);

        Assert.True(dto.ProductCreatedInSale);
        Assert.False(dto.CostToConfirm);
        var persisted = await context.FlightSegments.AsNoTracking().SingleAsync();
        Assert.False(persisted.CostToConfirm);
        Assert.Null(persisted.CostToConfirmReason);
    }

    [Fact]
    public async Task ConfirmTransferCostAsync_ResponseKeepsProductCreatedInSale_AndClearsMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        stored.RateId = rate.Id;
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        // Sin esto el test es VACUO: el contexto trackeado hace relationship fixup y deja la nav Rate
        // poblada, asi que el map ya daria ProductCreatedInSale=true ANTES del stamp explicito del
        // confirm. Clear simula el contexto fresco de produccion (FindAsync sin Include).
        context.ChangeTracker.Clear();

        var confirmer = CreateServiceForUser(context, mapper, canSeeCost: true, catalogFlagOn: true);
        var dto = await confirmer.ConfirmTransferCostAsync(reserva.Id.ToString(), stored.PublicId.ToString(), new ConfirmCostRequest(), CancellationToken.None);

        Assert.True(dto.ProductCreatedInSale);
        Assert.False(dto.CostToConfirm);
        var persisted = await context.TransferBookings.AsNoTracking().SingleAsync();
        Assert.False(persisted.CostToConfirm);
        Assert.Null(persisted.CostToConfirmReason);
    }

    [Fact]
    public async Task ConfirmPackageCostAsync_ResponseKeepsProductCreatedInSale_AndClearsMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        stored.RateId = rate.Id;
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        // Sin esto el test es VACUO: el contexto trackeado hace relationship fixup y deja la nav Rate
        // poblada, asi que el map ya daria ProductCreatedInSale=true ANTES del stamp explicito del
        // confirm. Clear simula el contexto fresco de produccion (FindAsync sin Include).
        context.ChangeTracker.Clear();

        var confirmer = CreateServiceForUser(context, mapper, canSeeCost: true, catalogFlagOn: true);
        var dto = await confirmer.ConfirmPackageCostAsync(reserva.Id.ToString(), stored.PublicId.ToString(), new ConfirmCostRequest(), CancellationToken.None);

        Assert.True(dto.ProductCreatedInSale);
        Assert.False(dto.CostToConfirm);
        var persisted = await context.PackageBookings.AsNoTracking().SingleAsync();
        Assert.False(persisted.CostToConfirm);
        Assert.Null(persisted.CostToConfirmReason);
    }

    [Fact]
    public async Task ConfirmAssistanceCostAsync_ResponseKeepsProductCreatedInSale_AndClearsMark()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var seeAll = CreateServiceForUser(context, mapper, canSeeCost: true);
        await seeAll.CreateAssistanceAsync(reserva.Id, BuildCreateAssistance(supplier.PublicId.ToString()), CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        stored.RateId = rate.Id;
        stored.CostToConfirm = true;
        stored.CostToConfirmReason = "NoKnownCost";
        await context.SaveChangesAsync();

        // Sin esto el test es VACUO: el contexto trackeado hace relationship fixup y deja la nav Rate
        // poblada, asi que el map ya daria ProductCreatedInSale=true ANTES del stamp explicito del
        // confirm. Clear simula el contexto fresco de produccion (FindAsync sin Include).
        context.ChangeTracker.Clear();

        var confirmer = CreateServiceForUser(context, mapper, canSeeCost: true, catalogFlagOn: true);
        var dto = await confirmer.ConfirmAssistanceCostAsync(reserva.Id.ToString(), stored.PublicId.ToString(), new ConfirmCostRequest(), CancellationToken.None);

        Assert.True(dto.ProductCreatedInSale);
        Assert.False(dto.CostToConfirm);
        var persisted = await context.AssistanceBookings.AsNoTracking().SingleAsync();
        Assert.False(persisted.CostToConfirm);
        Assert.Null(persisted.CostToConfirmReason);
    }

    // ============================================================ patron de flag (OFF = valor neutro) ============================================================

    [Fact]
    public async Task CreateHotelAsync_FlagOff_DoesNotSetCostToConfirm()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var service = CreateServiceForUser(context, mapper, canSeeCost: true);

        // Sin settings sembrados el flag EnableCatalogFindOrCreate esta OFF -> corre el path legacy,
        // que NUNCA escribe la marca. El campo viaja igual en el DTO, con valor neutro.
        var created = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        Assert.False(created.CostToConfirm);
        Assert.False((await context.HotelBookings.SingleAsync()).CostToConfirm);
    }

    [Fact]
    public async Task CreateHotelAsync_FlagOff_WithRateCreatedInSale_StillDerivesProductCreatedInSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        // Caso real: el producto nacio en venta mientras el flag estuvo ON; despues alguien apago el
        // flag y carga un servicio con ese rate desde el modal viejo. La derivacion es independiente
        // del flag (refleja el dato persistido).
        var rate = await SeedRateAsync(context, id: 50, createdInSale: true);
        var service = CreateServiceForUser(context, mapper, canSeeCost: true);

        var request = BuildCreateHotel(supplier.PublicId.ToString()) with { RateId = rate.PublicId.ToString() };
        var created = await service.CreateHotelAsync(reserva.Id, request, CancellationToken.None);

        Assert.True(created.ProductCreatedInSale);
    }

    // ============================================================ detalle de la reserva (ReservaService) ============================================================

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService BuildReservaService(AppDbContext context, IMapper mapper, IHttpContextAccessor accessor, IUserPermissionResolver resolver)
    {
        var settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        return new ReservaService(context, mapper, settingsServiceMock.Object, BuildUserManager(),
            NullLogger<ReservaService>.Instance, resolver, accessor);
    }

    // Reserva con dos hoteles: uno con producto creado en venta y la marca "a confirmar", otro sin rate.
    private static async Task SeedReservaWithPillDataAsync(AppDbContext context)
    {
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Proveedor test" });
        context.Rates.Add(new Rate
        {
            Id = 50,
            SupplierId = 1,
            ServiceType = "Hotel",
            ProductName = "Hotel creado en venta",
            CreatedInSale = true
        });
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0002",
            Name = "Reserva pills",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-1"
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 10,
            ReservaId = 1,
            SupplierId = 1,
            RateId = 50,
            HotelName = "Hotel en venta",
            City = "Bariloche",
            CheckIn = DateTime.UtcNow.Date.AddDays(10),
            CheckOut = DateTime.UtcNow.Date.AddDays(12),
            Nights = 2,
            SalePrice = 400m,
            NetCost = 250m,
            CostToConfirm = true,
            CostToConfirmReason = "NoKnownCost"
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 11,
            ReservaId = 1,
            SupplierId = 1,
            HotelName = "Hotel manual",
            City = "Bariloche",
            CheckIn = DateTime.UtcNow.Date.AddDays(10),
            CheckOut = DateTime.UtcNow.Date.AddDays(12),
            Nights = 2,
            SalePrice = 300m,
            NetCost = 200m
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Detail_UserWithoutSeeCost_MasksCostToConfirm_ButSeesProductCreatedInSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        await SeedReservaWithPillDataAsync(context);
        var accessor = BuildHttpContextAccessor("vendedor-1", "Vendedor");
        var resolver = BuildResolver("vendedor-1"); // sin see_cost
        var service = BuildReservaService(context, mapper, accessor, resolver);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        var conRate = dto.HotelBookings.Single(h => h.HotelName == "Hotel en venta");
        var sinRate = dto.HotelBookings.Single(h => h.HotelName == "Hotel manual");

        Assert.False(conRate.CostToConfirm);       // marca de costo oculta (guia UX linea 81)
        Assert.True(conRate.ProductCreatedInSale); // la pill violeta NO es dato de costo: se ve igual
        Assert.Equal(0m, conRate.NetCost);         // sanity: el costo sigue enmascarado
        Assert.False(sinRate.ProductCreatedInSale);
    }

    [Fact]
    public async Task Detail_UserWithSeeCost_SeesCostToConfirmAndProductCreatedInSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        await SeedReservaWithPillDataAsync(context);
        var accessor = BuildHttpContextAccessor("colab-1", "Colaborador");
        var resolver = BuildResolver("colab-1", Permissions.CobranzasSeeCost);
        var service = BuildReservaService(context, mapper, accessor, resolver);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        var conRate = dto.HotelBookings.Single(h => h.HotelName == "Hotel en venta");
        Assert.True(conRate.CostToConfirm);
        Assert.True(conRate.ProductCreatedInSale);
        Assert.Equal(250m, conRate.NetCost); // sin regresion para quien ve costos
    }

    [Fact]
    public async Task Detail_Admin_SeesCostToConfirm()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        await SeedReservaWithPillDataAsync(context);
        var accessor = BuildHttpContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1"); // Admin bypass por rol, sin permisos explicitos
        var service = BuildReservaService(context, mapper, accessor, resolver);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        var conRate = dto.HotelBookings.Single(h => h.HotelName == "Hotel en venta");
        Assert.True(conRate.CostToConfirm);
        Assert.True(conRate.ProductCreatedInSale);
    }
}
