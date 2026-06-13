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
/// Fuga 3 (ADR-017 §2.7, F1b — fix de seguridad SIN flag): un UPDATE de un caller sin
/// cobranzas.see_cost destruia el costo persistido. El GET le enmascara NetCost/Tax a 0,
/// el form de edicion se puebla con ese 0 y el submit lo manda de vuelta; el mapeo
/// automatico pisaba el costo real con 0 en cada edicion legitima. Contrato fijado,
/// por cada uno de los 5 tipos (Hotel/Aereo/Traslado/Paquete/Asistencia):
///   - caller SIN permiso: NetCost y Tax persistidos quedan INTACTOS aunque el request
///     traiga 0; la ganancia se recalcula canonica (SalePrice - NetCost - Tax) con los
///     valores PRESERVADOS (no con los del request); el resto de los campos del update
///     se aplica normal (puede editar venta, fechas, notas, etc.);
///   - caller CON permiso: los costos del request se persisten tal cual (comportamiento
///     identico al de siempre, sin regresion).
/// </summary>
public class BookingServiceCostPreservationTests
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
        // ADR-027: overload nuevo que pasan los paths de edicion (marca "confirmada con cambios").
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
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

    private static async Task<(Reserva reserva, Supplier supplier)> SeedReservaAndSupplierAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Proveedor test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9100", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    // ============================= HOTEL =============================
    // Costos sembrados: NetCost 250, Tax 40. Venta original 400.

    private static CreateHotelRequest BuildCreateHotel(string supplierPublicId) => new(
        SupplierId: supplierPublicId, HotelName: "Hotel test", StarRating: 4, City: "Bariloche", Country: "Argentina",
        CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
        RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
        NetCost: 250m, SalePrice: 400m, Commission: 110m, Notes: null, Tax: 40m);

    // El update que mandaria el form de un caller SIN ver-costos: NetCost/Tax rebotan en 0
    // y la Commission viene calculada con ese 0 (= SalePrice). Cambia venta y notas.
    private static UpdateHotelRequest BuildHotelUpdateFromMaskedForm(string supplierPublicId, decimal newSalePrice) => new(
        SupplierId: supplierPublicId, HotelName: "Hotel test", StarRating: 4, City: "Bariloche", Country: "Argentina",
        CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
        RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
        NetCost: 0m, SalePrice: newSalePrice, Commission: newSalePrice, Status: "Solicitado", Notes: "editado sin ver costos",
        Tax: 0m);

    [Fact]
    public async Task UpdateHotelAsync_UserWithoutSeeCost_PreservesCostsAndRecalculatesCommission()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var dto = await noCost.UpdateHotelAsync(
            reserva.Id.ToString(), created.PublicId.ToString(),
            BuildHotelUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 500m), CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        Assert.Equal(250m, stored.NetCost);            // costo preservado (el 0 del request era el masking rebotado)
        Assert.Equal(40m, stored.Tax);                 // impuesto preservado
        Assert.Equal(500m - 250m - 40m, stored.Commission); // ganancia recalculada con los PRESERVADOS
        Assert.Equal(500m, stored.SalePrice);          // la venta SI se aplica (el caller la ve)
        Assert.Equal("editado sin ver costos", stored.Notes); // el resto del update sigue funcionando
        Assert.Equal(0m, dto.NetCost);                 // y el response sigue enmascarado
    }

    [Fact]
    public async Task UpdateHotelAsync_UserWithSeeCost_AppliesRequestCosts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString()), CancellationToken.None);

        var update = BuildHotelUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 500m)
            with { NetCost = 300m, Tax = 50m, Commission = 150m };
        await seeder.UpdateHotelAsync(reserva.Id.ToString(), created.PublicId.ToString(), update, CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        // Con permiso: el request manda, igual que siempre (incluida la Commission del request).
        Assert.Equal(300m, stored.NetCost);
        Assert.Equal(50m, stored.Tax);
        Assert.Equal(150m, stored.Commission);
        Assert.Equal(500m, stored.SalePrice);
    }

    // ============================= FLIGHT =============================
    // Costos sembrados: NetCost 300, Tax 30. Venta original 500.

    private static CreateFlightRequest BuildCreateFlight(string supplierPublicId) => new(
        SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
        Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
        DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
        CabinClass: "Economy", Baggage: null, PNR: null,
        NetCost: 300m, SalePrice: 500m, Commission: 170m, Tax: 30m, Notes: null);

    private static UpdateFlightRequest BuildFlightUpdateFromMaskedForm(string supplierPublicId, decimal newSalePrice) => new(
        SupplierId: supplierPublicId, AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
        Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
        DepartureTime: DateTime.UtcNow.Date.AddDays(11), ArrivalTime: DateTime.UtcNow.Date.AddDays(11).AddHours(2),
        CabinClass: "Economy", Baggage: null, TicketNumber: null, PNR: null,
        NetCost: 0m, SalePrice: newSalePrice, Commission: newSalePrice, Tax: 0m, Status: "HL",
        Notes: "editado sin ver costos");

    [Fact]
    public async Task UpdateFlightAsync_UserWithoutSeeCost_PreservesCostsAndRecalculatesCommission()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString()), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        await noCost.UpdateFlightAsync(
            reserva.Id.ToString(), created.PublicId.ToString(),
            BuildFlightUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 600m), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(300m, stored.NetCost);
        Assert.Equal(30m, stored.Tax);
        Assert.Equal(600m - 300m - 30m, stored.Commission);
        Assert.Equal(600m, stored.SalePrice);
        Assert.Equal("editado sin ver costos", stored.Notes);
    }

    [Fact]
    public async Task UpdateFlightAsync_UserWithSeeCost_AppliesRequestCosts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreateFlightAsync(reserva.Id, BuildCreateFlight(supplier.PublicId.ToString()), CancellationToken.None);

        var update = BuildFlightUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 600m)
            with { NetCost = 350m, Tax = 35m, Commission = 215m };
        await seeder.UpdateFlightAsync(reserva.Id.ToString(), created.PublicId.ToString(), update, CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal(350m, stored.NetCost);
        Assert.Equal(35m, stored.Tax);
        Assert.Equal(215m, stored.Commission);
    }

    // ============================= TRANSFER =============================
    // Costos sembrados: NetCost 70, Tax 12. Venta original 120.

    private static CreateTransferRequest BuildCreateTransfer(string supplierPublicId) => new(
        SupplierId: supplierPublicId, PickupLocation: "Aeropuerto", DropoffLocation: "Hotel centro",
        PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Sedan", Passengers: 2,
        IsRoundTrip: false, ReturnDateTime: null,
        NetCost: 70m, SalePrice: 120m, Commission: 38m, Notes: null, Tax: 12m);

    private static UpdateTransferRequest BuildTransferUpdateFromMaskedForm(string supplierPublicId, decimal newSalePrice) => new(
        SupplierId: supplierPublicId, PickupLocation: "Aeropuerto", DropoffLocation: "Hotel centro",
        PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Van", Passengers: 4,
        IsRoundTrip: false, ReturnDateTime: null, ConfirmationNumber: null,
        NetCost: 0m, SalePrice: newSalePrice, Commission: newSalePrice, Status: "Solicitado", Notes: null,
        Tax: 0m);

    [Fact]
    public async Task UpdateTransferAsync_UserWithoutSeeCost_PreservesCostsAndRecalculatesCommission()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString()), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        await noCost.UpdateTransferAsync(
            reserva.Id.ToString(), created.PublicId.ToString(),
            BuildTransferUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 150m), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        Assert.Equal(70m, stored.NetCost);
        Assert.Equal(12m, stored.Tax);
        Assert.Equal(150m - 70m - 12m, stored.Commission);
        Assert.Equal(150m, stored.SalePrice);
        Assert.Equal("Van", stored.VehicleType); // el resto del update sigue funcionando
    }

    [Fact]
    public async Task UpdateTransferAsync_UserWithSeeCost_AppliesRequestCosts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreateTransferAsync(reserva.Id, BuildCreateTransfer(supplier.PublicId.ToString()), CancellationToken.None);

        var update = BuildTransferUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 150m)
            with { NetCost = 90m, Tax = 15m, Commission = 45m };
        await seeder.UpdateTransferAsync(reserva.Id.ToString(), created.PublicId.ToString(), update, CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        Assert.Equal(90m, stored.NetCost);
        Assert.Equal(15m, stored.Tax);
        Assert.Equal(45m, stored.Commission);
    }

    // ============================= PACKAGE =============================
    // Costos sembrados: NetCost 900, Tax 60. Venta original 1500.

    private static CreatePackageRequest BuildCreatePackage(string supplierPublicId) => new(
        SupplierId: supplierPublicId, PackageName: "Paquete Caribe", Destination: "Cancun",
        StartDate: DateTime.UtcNow.Date.AddDays(20), EndDate: DateTime.UtcNow.Date.AddDays(27),
        IncludesHotel: true, IncludesFlight: true, IncludesTransfer: true, IncludesExcursions: false, IncludesMeals: true,
        Adults: 2, Children: 0, Itinerary: null,
        NetCost: 900m, SalePrice: 1500m, Commission: 540m, Notes: null, Tax: 60m);

    private static UpdatePackageRequest BuildPackageUpdateFromMaskedForm(string supplierPublicId, decimal newSalePrice) => new(
        SupplierId: supplierPublicId, PackageName: "Paquete Caribe", Destination: "Cancun",
        StartDate: DateTime.UtcNow.Date.AddDays(20), EndDate: DateTime.UtcNow.Date.AddDays(27),
        IncludesHotel: true, IncludesFlight: true, IncludesTransfer: true, IncludesExcursions: false, IncludesMeals: true,
        Adults: 2, Children: 1, Itinerary: null, ConfirmationNumber: null,
        NetCost: 0m, SalePrice: newSalePrice, Commission: newSalePrice, Status: "Solicitado", Notes: null,
        Tax: 0m);

    [Fact]
    public async Task UpdatePackageAsync_UserWithoutSeeCost_PreservesCostsAndRecalculatesCommission()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString()), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        await noCost.UpdatePackageAsync(
            reserva.Id.ToString(), created.PublicId.ToString(),
            BuildPackageUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 1800m), CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        Assert.Equal(900m, stored.NetCost);
        Assert.Equal(60m, stored.Tax);
        Assert.Equal(1800m - 900m - 60m, stored.Commission);
        Assert.Equal(1800m, stored.SalePrice);
        Assert.Equal(1, stored.Children); // el resto del update sigue funcionando
    }

    [Fact]
    public async Task UpdatePackageAsync_UserWithSeeCost_AppliesRequestCosts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreatePackageAsync(reserva.Id, BuildCreatePackage(supplier.PublicId.ToString()), CancellationToken.None);

        var update = BuildPackageUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 1800m)
            with { NetCost = 1000m, Tax = 80m, Commission = 720m };
        await seeder.UpdatePackageAsync(reserva.Id.ToString(), created.PublicId.ToString(), update, CancellationToken.None);

        var stored = await context.PackageBookings.SingleAsync();
        Assert.Equal(1000m, stored.NetCost);
        Assert.Equal(80m, stored.Tax);
        Assert.Equal(720m, stored.Commission);
    }

    // ============================= ASSISTANCE =============================
    // Costos sembrados: NetCost 100, Tax 10. Venta original 250.

    private static CreateAssistanceRequest BuildCreateAssistance(string supplierPublicId) => new(
        SupplierId: supplierPublicId,
        ValidFrom: DateTime.UtcNow.Date.AddDays(10), ValidTo: DateTime.UtcNow.Date.AddDays(20),
        Adults: 2, Children: 0,
        NetCost: 100m, SalePrice: 250m, Commission: 140m,
        PlanType: "Premium 60K", Tax: 10m);

    private static UpdateAssistanceRequest BuildAssistanceUpdateFromMaskedForm(string supplierPublicId, decimal newSalePrice) => new(
        SupplierId: supplierPublicId,
        ValidFrom: DateTime.UtcNow.Date.AddDays(10), ValidTo: DateTime.UtcNow.Date.AddDays(20),
        Adults: 2, Children: 0,
        NetCost: 0m, SalePrice: newSalePrice, Commission: newSalePrice,
        Status: "Solicitado", PlanType: "Premium 150K", Tax: 0m);

    [Fact]
    public async Task UpdateAssistanceAsync_UserWithoutSeeCost_PreservesCostsAndRecalculatesCommission()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreateAssistanceAsync(reserva.Id, BuildCreateAssistance(supplier.PublicId.ToString()), CancellationToken.None);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        await noCost.UpdateAssistanceAsync(
            reserva.Id.ToString(), created.PublicId.ToString(),
            BuildAssistanceUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 300m), CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        Assert.Equal(100m, stored.NetCost);
        Assert.Equal(10m, stored.Tax);
        Assert.Equal(300m - 100m - 10m, stored.Commission);
        Assert.Equal(300m, stored.SalePrice);
        Assert.Equal("Premium 150K", stored.PlanType); // el resto del update sigue funcionando
    }

    [Fact]
    public async Task UpdateAssistanceAsync_UserWithSeeCost_AppliesRequestCosts()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var seeder = CreateServiceForUser(context, mapper, canSeeCost: true);
        var created = await seeder.CreateAssistanceAsync(reserva.Id, BuildCreateAssistance(supplier.PublicId.ToString()), CancellationToken.None);

        var update = BuildAssistanceUpdateFromMaskedForm(supplier.PublicId.ToString(), newSalePrice: 300m)
            with { NetCost = 120m, Tax = 12m, Commission = 168m };
        await seeder.UpdateAssistanceAsync(reserva.Id.ToString(), created.PublicId.ToString(), update, CancellationToken.None);

        var stored = await context.AssistanceBookings.SingleAsync();
        Assert.Equal(120m, stored.NetCost);
        Assert.Equal(12m, stored.Tax);
        Assert.Equal(168m, stored.Commission);
    }

    // ============================= HOTEL CREATE desde tarifario (B1) =============================
    // Regresion introducida por el masking de Fuga 1: el search del tarifario enmascara
    // NetCost/Tax a 0 para el caller sin ver-costos, el form copia ese 0 al create y, como
    // ApplyHotelRateSnapshot NO re-aplica precios (a diferencia de Flight/Package/Transfer/
    // Assistance), el hotel nacia con costo 0. Contrato fijado:
    //   - caller SIN permiso + RateId: el server resuelve NetCost/Tax desde la tarifa
    //     (precio por noche/habitacion -> total = unitario x noches x habitaciones) y
    //     recalcula la ganancia canonica con el SalePrice del request;
    //   - caller CON permiso: el request manda, identico al comportamiento de siempre;
    //   - tarifa sin costo utilizable: se persiste 0 (no inventar).

    // Tarifa de hotel: precio UNITARIO (por noche/habitacion). NetCost 100, Tax 15, Venta 160.
    private static async Task<Rate> SeedHotelRateAsync(AppDbContext context, decimal netCost = 100m, decimal tax = 15m)
    {
        var rate = new Rate
        {
            Id = 50,
            SupplierId = 1, // el proveedor sembrado por SeedReservaAndSupplierAsync
            ServiceType = "Hotel",
            ProductName = "Hotel test doble",
            HotelName = "Hotel test",
            City = "Bariloche",
            RoomType = "Doble",
            MealPlan = "Desayuno",
            NetCost = netCost,
            Tax = tax,
            SalePrice = 160m,
            Commission = 45m,
            IsActive = true
        };
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return rate;
    }

    // El create que mandaria el form de un caller SIN ver-costos al elegir una tarifa:
    // netCost/tax rebotan en 0 (masking del search) y la Commission viene = SalePrice.
    // 2 noches (checkin +10 / checkout +12) x 1 habitacion = 2 unidades de tarifa.
    private static CreateHotelRequest BuildCreateHotelFromMaskedRate(string supplierPublicId, string rateId, decimal salePrice) => new(
        SupplierId: supplierPublicId, HotelName: "Hotel test", StarRating: 4, City: "Bariloche", Country: "Argentina",
        CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
        RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
        NetCost: 0m, SalePrice: salePrice, Commission: salePrice, Notes: null,
        RateId: rateId, Tax: 0m);

    [Fact]
    public async Task CreateHotelAsync_FromRate_UserWithoutSeeCost_ResolvesCostsFromRate()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedHotelRateAsync(context);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        var dto = await noCost.CreateHotelAsync(
            reserva.Id,
            BuildCreateHotelFromMaskedRate(supplier.PublicId.ToString(), rate.PublicId.ToString(), salePrice: 400m),
            CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        Assert.Equal(200m, stored.NetCost);              // 100 por noche x 2 noches x 1 habitacion
        Assert.Equal(30m, stored.Tax);                   // 15 por noche x 2 noches x 1 habitacion
        Assert.Equal(400m - 200m - 30m, stored.Commission); // ganancia canonica con la venta del request
        Assert.Equal(400m, stored.SalePrice);            // la venta del request manda (el caller la ve)
        Assert.Equal(rate.Id, stored.RateId);            // la tarifa queda vinculada (snapshot)
        Assert.Equal(0m, dto.NetCost);                   // el response sigue enmascarado
        Assert.Equal(0m, dto.Tax);
    }

    [Fact]
    public async Task CreateHotelAsync_FromRate_UserWithSeeCost_RequestWins()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var rate = await SeedHotelRateAsync(context);

        var seeCost = CreateServiceForUser(context, mapper, canSeeCost: true);
        var request = BuildCreateHotelFromMaskedRate(supplier.PublicId.ToString(), rate.PublicId.ToString(), salePrice: 400m)
            with { NetCost = 250m, Tax = 40m, Commission = 110m };
        await seeCost.CreateHotelAsync(reserva.Id, request, CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        // Con permiso: el request manda, como siempre (la tarifa NO pisa los costos en Hotel).
        Assert.Equal(250m, stored.NetCost);
        Assert.Equal(40m, stored.Tax);
        Assert.Equal(110m, stored.Commission);
        Assert.Equal(rate.Id, stored.RateId);
    }

    [Fact]
    public async Task CreateHotelAsync_FromRateWithoutUsableCost_UserWithoutSeeCost_PersistsZero()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        // Tarifa sin costo cargado: no hay dato real que resolver -> queda 0, no se inventa.
        var rate = await SeedHotelRateAsync(context, netCost: 0m, tax: 0m);

        var noCost = CreateServiceForUser(context, mapper, canSeeCost: false);
        await noCost.CreateHotelAsync(
            reserva.Id,
            BuildCreateHotelFromMaskedRate(supplier.PublicId.ToString(), rate.PublicId.ToString(), salePrice: 400m),
            CancellationToken.None);

        var stored = await context.HotelBookings.SingleAsync();
        Assert.Equal(0m, stored.NetCost);
        Assert.Equal(0m, stored.Tax);
        Assert.Equal(400m, stored.Commission); // canonica: 400 - 0 - 0
    }
}
