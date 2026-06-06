using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-018 (2026-06-06): reconcilia la ficha "producto-primero" (un solo campo de busqueda por servicio)
/// con el esquema estructurado de los bookings no-Hotel. Cubre:
///  - el alta por catalogo SIN campos estructurados (persiste, identidad poblada, CabinClass/VehicleType
///    coalescidos, Nights=0 y schedule OK con EndDate null);
///  - los fallbacks downstream a ProductName en voucher, alertas y reportes (que antes mostraban basura
///    como "Aereo -" o " -> " y perdian revenue del ranking);
///  - el contrato unico <see cref="ServiceDisplayName"/>.
///
/// NOTA: el provider InMemory NO valida NOT NULL ni maxlength, asi que el "antes daba HTTP 500" (violacion
/// de constraint en Postgres) NO se reproduce aca; eso lo cubre la migracion M5 en el VPS. Estos tests
/// validan la LOGICA (identidad, coalesce, fallbacks), que es lo que vive en el codigo.
/// </summary>
public class Adr018ProductFirstReconciliationTests
{
    // ======================================================================================
    // ServiceDisplayName: el contrato unico (§4-ter). Pruebas puras, sin DB.
    // ======================================================================================

    [Fact]
    public void FirstNonBlank_ReturnsFirstWithText_AndEmptyWhenAllBlank()
    {
        Assert.Equal("Producto", ServiceDisplayName.FirstNonBlank(null, "  ", "Producto", "otro"));
        Assert.Equal(string.Empty, ServiceDisplayName.FirstNonBlank(null, "", "   "));
    }

    [Fact]
    public void RouteOrEmpty_OnlyBuildsArrow_WhenBothEndsPresent()
    {
        Assert.Equal("AEP -> IGR", ServiceDisplayName.RouteOrEmpty("AEP", "IGR"));
        // Falta un extremo => cadena vacia (asi el caller cae al ProductName en vez de mostrar " -> ").
        Assert.Equal(string.Empty, ServiceDisplayName.RouteOrEmpty("AEP", null));
        Assert.Equal(string.Empty, ServiceDisplayName.RouteOrEmpty(null, null));
    }

    [Fact]
    public void ForFlight_PrefersProductName_ThenAirlineAndNumber()
    {
        Assert.Equal("AEP-IGR LATAM", ServiceDisplayName.ForFlight("AEP-IGR LATAM", null, null));
        Assert.Equal("AR1234", ServiceDisplayName.ForFlight(null, "AR", "1234"));
    }

    [Fact]
    public void ForTransfer_PrefersProductName_ThenRoute_ThenVehicle()
    {
        Assert.Equal("Traslado VIP", ServiceDisplayName.ForTransfer("Traslado VIP", null, null, "Sedan"));
        Assert.Equal("Aeropuerto -> Hotel", ServiceDisplayName.ForTransfer(null, "Aeropuerto", "Hotel", "Sedan"));
        Assert.Equal("Sedan", ServiceDisplayName.ForTransfer(null, null, null, "Sedan"));
    }

    [Fact]
    public void ForPackage_PrefersPackageName_ThenDestination()
    {
        Assert.Equal("Caribe Magico", ServiceDisplayName.ForPackage("Caribe Magico", "Cancun"));
        // PackageName vacio => cae al destino (secundario).
        Assert.Equal("Cancun", ServiceDisplayName.ForPackage(null, "Cancun"));
        // Ambos vacios => cadena vacia (no inventa identidad).
        Assert.Equal(string.Empty, ServiceDisplayName.ForPackage(null, null));
    }

    // ======================================================================================
    // MappingProfile + ReservaScheduleCalculator: EndDate null no inventa fecha.
    // ======================================================================================

    [Fact]
    public void Mapping_PackageWithNullEndDate_NightsIsZero()
    {
        var mapper = CreateMapper();
        var start = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        var request = PackageRequest(packageName: "Caribe", destination: null, start: start, end: null);

        var package = mapper.Map<PackageBooking>(request);

        Assert.Equal(0, package.Nights);
        Assert.Null(package.EndDate);
    }

    [Fact]
    public async Task Schedule_PackageWithNullEndDate_UsesStartDateAsEnd()
    {
        await using var context = CreateContext();
        var start = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "R-1", Name = "Solo paquete" });
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 1, ReservaId = 1, PackageName = "Caribe", SupplierId = 1, StartDate = start, EndDate = null
        });
        await context.SaveChangesAsync();

        var (computedStart, computedEnd) = await ReservaScheduleCalculator.ComputeAsync(context, 1);

        Assert.Equal(start, computedStart);
        Assert.Equal(start, computedEnd); // EndDate null => se coalesce a StartDate, no inventa fecha
    }

    // ======================================================================================
    // Alta por catalogo SIN campos estructurados: identidad poblada + coalesce de defaults.
    // ======================================================================================

    [Fact]
    public async Task CreateFlightWithCatalog_NoStructuredFields_PersistsWithProductNameAndDefaultCabin()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: true, canSeeCost: true);

        var request = new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: null, AirlineName: null, FlightNumber: null,
            Origin: null, OriginCity: null, Destination: null, DestinationCity: null,
            DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass: "", Baggage: null, PNR: null,
            NetCost: 500m, SalePrice: 900m, Commission: 400m, Tax: 0m, Notes: null,
            Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest("AEP-IGR LATAM", "Iguazu", supplier.PublicId.ToString()),
            ProductName: "AEP-IGR LATAM");

        var dto = await service.CreateFlightAsync(reserva.Id, request, CancellationToken.None);

        var flight = await context.FlightSegments.SingleAsync();
        Assert.Equal("AEP-IGR LATAM", flight.ProductName);
        Assert.Equal("AEP-IGR LATAM", dto.ProductName);
        Assert.Equal("Economy", flight.CabinClass); // CabinClass vacio => default de negocio
        Assert.Null(flight.Origin);
    }

    [Fact]
    public async Task CreateTransferWithCatalog_NoStructuredFields_PersistsWithProductNameAndDefaultVehicle()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: true, canSeeCost: true);

        var request = new CreateTransferRequest(
            SupplierId: supplier.PublicId.ToString(),
            PickupLocation: null, DropoffLocation: null,
            PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "",
            Passengers: 2, IsRoundTrip: false, ReturnDateTime: null,
            NetCost: 100m, SalePrice: 180m, Commission: 80m, Notes: null,
            Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest("Traslado privado aeropuerto", "Iguazu", supplier.PublicId.ToString()),
            ProductName: "Traslado privado aeropuerto");

        var dto = await service.CreateTransferAsync(reserva.Id, request, CancellationToken.None);

        var transfer = await context.TransferBookings.SingleAsync();
        Assert.Equal("Traslado privado aeropuerto", transfer.ProductName);
        Assert.Equal("Traslado privado aeropuerto", dto.ProductName);
        Assert.Equal("Sedan", transfer.VehicleType); // VehicleType vacio => default de negocio
        Assert.Null(transfer.PickupLocation);
    }

    [Fact]
    public async Task CreatePackageWithCatalog_NoDestinationNoEndDate_PersistsWithNightsZero()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: true, canSeeCost: true);

        var request = PackageRequest(
            packageName: "Caribe Magico", destination: null,
            start: DateTime.UtcNow.Date.AddDays(10), end: null,
            supplierPublicId: supplier.PublicId.ToString(),
            newProduct: new NewCatalogProductRequest("Caribe Magico", "Caribe", supplier.PublicId.ToString()));

        var dto = await service.CreatePackageAsync(reserva.Id, request, CancellationToken.None);

        var package = await context.PackageBookings.SingleAsync();
        Assert.Equal("Caribe Magico", package.PackageName);
        Assert.Null(package.Destination);
        Assert.Null(package.EndDate);
        Assert.Equal(0, package.Nights);
        Assert.Null(dto.EndDate);
    }

    [Fact]
    public async Task CreateFlight_FlagOff_NullStructuredFields_PersistsAndKeepsProductName()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: false, canSeeCost: true);

        var request = new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: null, AirlineName: null, FlightNumber: null,
            Origin: null, OriginCity: null, Destination: null, DestinationCity: null,
            DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass: "Economy", Baggage: null, PNR: null,
            NetCost: 500m, SalePrice: 900m, Commission: 400m, Tax: 0m, Notes: null,
            ProductName: "Vuelo directo");

        await service.CreateFlightAsync(reserva.Id, request, CancellationToken.None);

        var flight = await context.FlightSegments.SingleAsync();
        Assert.Equal("Vuelo directo", flight.ProductName); // el map de convencion copia ProductName tambien con flag OFF
        Assert.Null(flight.AirlineCode);
    }

    // ======================================================================================
    // Anti-clobber de ProductName en el UPDATE: el modal viejo edita estos servicios SIN mandar
    // ProductName (llega null). El map lo IGNORA y el service lo asigna a mano solo si viene con
    // valor, para que la edicion vieja no borre la identidad "producto-primero".
    // ======================================================================================

    [Fact]
    public async Task UpdateFlight_OldModalSendsNullProductName_PreservesPersistedIdentity()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: true, canSeeCost: true);

        // Alta por catalogo: identidad = ProductName (sin aerolinea/numero).
        var created = await service.CreateFlightAsync(reserva.Id, new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: null, AirlineName: null, FlightNumber: null,
            Origin: null, OriginCity: null, Destination: null, DestinationCity: null,
            DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass: "Economy", Baggage: null, PNR: null,
            NetCost: 500m, SalePrice: 900m, Commission: 400m, Tax: 0m, Notes: null,
            Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest("AEP-IGR LATAM", "Iguazu", supplier.PublicId.ToString()),
            ProductName: "AEP-IGR LATAM"), CancellationToken.None);

        // El modal viejo reenvia el form SIN ProductName (null).
        await service.UpdateFlightAsync(
            reserva.Id.ToString(), created.PublicId.ToString(),
            BuildFlightUpdate(supplier.PublicId.ToString(), productName: null, salePrice: 950m), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal("AEP-IGR LATAM", stored.ProductName); // preservado: el null NO lo borro
        Assert.Equal(950m, stored.SalePrice);              // el resto del update si se aplica
    }

    [Fact]
    public async Task UpdateFlight_InlineSendsProductName_UpdatesIdentity()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: true, canSeeCost: true);

        var created = await service.CreateFlightAsync(reserva.Id, new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: null, AirlineName: null, FlightNumber: null,
            Origin: null, OriginCity: null, Destination: null, DestinationCity: null,
            DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass: "Economy", Baggage: null, PNR: null,
            NetCost: 500m, SalePrice: 900m, Commission: 400m, Tax: 0m, Notes: null,
            Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest("AEP-IGR LATAM", "Iguazu", supplier.PublicId.ToString()),
            ProductName: "AEP-IGR LATAM"), CancellationToken.None);

        // La ficha inline reenvia un ProductName con valor (el vendedor lo corrigio).
        await service.UpdateFlightAsync(
            reserva.Id.ToString(), created.PublicId.ToString(),
            BuildFlightUpdate(supplier.PublicId.ToString(), productName: "AEP-IGR Aerolineas", salePrice: 900m), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal("AEP-IGR Aerolineas", stored.ProductName); // el valor nuevo SI actualiza
    }

    [Fact]
    public async Task UpdateTransfer_OldModalSendsNullProductName_PreservesPersistedIdentity()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: true, canSeeCost: true);

        var created = await service.CreateTransferAsync(reserva.Id, new CreateTransferRequest(
            SupplierId: supplier.PublicId.ToString(),
            PickupLocation: null, DropoffLocation: null,
            PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Sedan",
            Passengers: 2, IsRoundTrip: false, ReturnDateTime: null,
            NetCost: 100m, SalePrice: 180m, Commission: 80m, Notes: null,
            Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest("Traslado privado aeropuerto", "Iguazu", supplier.PublicId.ToString()),
            ProductName: "Traslado privado aeropuerto"), CancellationToken.None);

        await service.UpdateTransferAsync(
            reserva.Id.ToString(), created.PublicId.ToString(),
            BuildTransferUpdate(supplier.PublicId.ToString(), productName: null, salePrice: 200m), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        Assert.Equal("Traslado privado aeropuerto", stored.ProductName); // preservado
        Assert.Equal(200m, stored.SalePrice);
    }

    [Fact]
    public async Task UpdateTransfer_InlineSendsProductName_UpdatesIdentity()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: true, canSeeCost: true);

        var created = await service.CreateTransferAsync(reserva.Id, new CreateTransferRequest(
            SupplierId: supplier.PublicId.ToString(),
            PickupLocation: null, DropoffLocation: null,
            PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Sedan",
            Passengers: 2, IsRoundTrip: false, ReturnDateTime: null,
            NetCost: 100m, SalePrice: 180m, Commission: 80m, Notes: null,
            Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest("Traslado privado aeropuerto", "Iguazu", supplier.PublicId.ToString()),
            ProductName: "Traslado privado aeropuerto"), CancellationToken.None);

        await service.UpdateTransferAsync(
            reserva.Id.ToString(), created.PublicId.ToString(),
            BuildTransferUpdate(supplier.PublicId.ToString(), productName: "Traslado VIP", salePrice: 180m), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        Assert.Equal("Traslado VIP", stored.ProductName);
    }

    // ======================================================================================
    // Coalesce de defaults en el path NO-catalogo (legacy, flag OFF): CabinClass/VehicleType
    // vacios deben caer al default de negocio, igual que el path catalogo (no persistir "").
    // ======================================================================================

    [Fact]
    public async Task CreateFlight_LegacyPath_EmptyCabinClass_CoalescesToEconomy()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: false, canSeeCost: true);

        await service.CreateFlightAsync(reserva.Id, new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass: "", Baggage: null, PNR: null,
            NetCost: 500m, SalePrice: 900m, Commission: 400m, Tax: 0m, Notes: null), CancellationToken.None);

        var stored = await context.FlightSegments.SingleAsync();
        Assert.Equal("Economy", stored.CabinClass); // "" => default, no se persiste vacio
    }

    [Fact]
    public async Task CreateTransfer_LegacyPath_EmptyVehicleType_CoalescesToSedan()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var (reserva, supplier) = await SeedAsync(context);
        var service = BuildBookingService(context, mapper, flagOn: false, canSeeCost: true);

        await service.CreateTransferAsync(reserva.Id, new CreateTransferRequest(
            SupplierId: supplier.PublicId.ToString(),
            PickupLocation: "Aeropuerto", DropoffLocation: "Hotel",
            PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "",
            Passengers: 2, IsRoundTrip: false, ReturnDateTime: null,
            NetCost: 100m, SalePrice: 180m, Commission: 80m, Notes: null), CancellationToken.None);

        var stored = await context.TransferBookings.SingleAsync();
        Assert.Equal("Sedan", stored.VehicleType); // "" => default, no se persiste vacio
    }

    // ======================================================================================
    // Voucher (HTML): con estructurados null muestra ProductName, no titulo vacio.
    // ======================================================================================

    [Fact]
    public async Task VoucherHtml_CatalogServices_ShowProductNameInsteadOfBlankTitles()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, PublicId = Guid.NewGuid(), NumeroReserva = "R-VCH", Name = "Reserva voucher" };
        context.Reservas.Add(reserva);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 1, ReservaId = 1, SupplierId = 1, ProductName = "AEP-IGR LATAM",
            DepartureTime = DateTime.UtcNow.Date.AddDays(10)
        });
        context.TransferBookings.Add(new TransferBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1, ProductName = "Traslado VIP",
            VehicleType = "Sedan", PickupDateTime = DateTime.UtcNow.Date.AddDays(10)
        });
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1, PackageName = "Caribe Magico",
            Destination = null, StartDate = DateTime.UtcNow.Date.AddDays(10), EndDate = null
        });
        await context.SaveChangesAsync();

        var voucherService = new VoucherService(
            context, Mock.Of<IOperationalFinanceSettingsService>(), Mock.Of<IFileStoragePort>());
        var html = Encoding.UTF8.GetString(await voucherService.GenerateVoucherHtmlAsync(1, CancellationToken.None));

        Assert.Contains("AEP-IGR LATAM", html);
        Assert.Contains("Traslado VIP", html);
        Assert.Contains("Caribe Magico", html);
        // No debe quedar el patron " -> " con extremos vacios (ruta sin datos).
        Assert.DoesNotContain(" -&gt; ", html);
        Assert.DoesNotContain(" -> ", html);
    }

    // ======================================================================================
    // AlertService: vuelo de catalogo usa ProductName, no "Aereo -".
    // ======================================================================================

    [Fact]
    public async Task AlertService_CatalogFlightDeadline_LabelUsesProductName()
    {
        await using var context = CreateContext();
        var today = DateTime.UtcNow.Date;
        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "R-ALERT", Name = "Reserva alerta",
            Status = EstadoReserva.Confirmed, StartDate = today.AddDays(20)
        });
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 1, ReservaId = 1, SupplierId = 1, ProductName = "AEP-IGR LATAM",
            Status = "HK", PNR = null, TicketingDeadline = today.AddDays(3)
        });
        await context.SaveChangesAsync();

        var alertService = BuildAlertService(context, deadlineAlerts: true);
        var payload = await alertService.GetAlertsAsync(new AlertCallerContext("admin", IsAdmin: true), CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "ServiceDeadlines"));
        var label = Prop<string>(item, "ServiceLabel");
        Assert.Contains("AEP-IGR LATAM", label);
        Assert.DoesNotContain("Aereo -", label); // antes emitia "Aereo -" con los estructurados null
    }

    // ======================================================================================
    // ReportService: paquete/vuelo de catalogo aparecen en el ranking (no se pierde revenue).
    // ======================================================================================

    [Fact]
    public async Task ReportService_CatalogServicesWithNullDestination_AppearInRankingViaProductName()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 1, ReservaId = 1, PackageName = "Caribe Magico", Destination = null,
            SalePrice = 2000m, NetCost = 1500m, Adults = 2, Children = 0, CreatedAt = now
        });
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 1, ReservaId = 1, ProductName = "AEP-IGR LATAM",
            Destination = null, DestinationCity = null,
            SalePrice = 900m, NetCost = 600m, CreatedAt = now
        });
        await context.SaveChangesAsync();

        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((BnaUsdSellerRateDto?)null);
        var reportService = new ReportService(context, bna.Object);

        var ranking = await reportService.GetDestinationAnalyticsAsync(null, null, CancellationToken.None);

        // La clave del ranking se normaliza con Trim().ToUpper().
        Assert.Contains(ranking, d => d.Destination == "CARIBE MAGICO");
        Assert.Contains(ranking, d => d.Destination == "AEP-IGR LATAM");
    }

    // ======================================================================================
    // Helpers de armado (espejo de BookingServiceCatalogTests, recortado a lo que usan estos tests).
    // ======================================================================================

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

    private static async Task<(Reserva reserva, Supplier supplier)> SeedAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Operador A" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-ADR018", Name = "Reserva ADR-018" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    // Update de vuelo tal como lo manda el form. productName=null simula el modal viejo (no envia el
    // campo); con valor simula la ficha inline. Los estructurados van null (vuelo de catalogo).
    private static UpdateFlightRequest BuildFlightUpdate(string supplierPublicId, string? productName, decimal salePrice)
        => new(
            SupplierId: supplierPublicId,
            AirlineCode: null, AirlineName: null, FlightNumber: null,
            Origin: null, OriginCity: null, Destination: null, DestinationCity: null,
            DepartureTime: DateTime.UtcNow.Date.AddDays(10), ArrivalTime: DateTime.UtcNow.Date.AddDays(10).AddHours(2),
            CabinClass: "Economy", Baggage: null, TicketNumber: null, PNR: null,
            NetCost: 500m, SalePrice: salePrice, Commission: 400m, Tax: 0m, Status: "HL", Notes: null,
            ProductName: productName);

    // Update de traslado. productName=null simula el modal viejo; con valor, la ficha inline.
    private static UpdateTransferRequest BuildTransferUpdate(string supplierPublicId, string? productName, decimal salePrice)
        => new(
            SupplierId: supplierPublicId,
            PickupLocation: null, DropoffLocation: null,
            PickupDateTime: DateTime.UtcNow.Date.AddDays(10), FlightNumber: null, VehicleType: "Sedan", Passengers: 2,
            IsRoundTrip: false, ReturnDateTime: null, ConfirmationNumber: null,
            NetCost: 100m, SalePrice: salePrice, Commission: 80m, Status: "Solicitado", Notes: null,
            ProductName: productName);

    private static CreatePackageRequest PackageRequest(
        string packageName, string? destination, DateTime start, DateTime? end,
        string supplierPublicId = "", NewCatalogProductRequest? newProduct = null)
        => new(
            SupplierId: supplierPublicId, PackageName: packageName, Destination: destination,
            StartDate: start, EndDate: end,
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false, IncludesMeals: false,
            Adults: 2, Children: 0, Itinerary: null,
            NetCost: 1500m, SalePrice: 2000m, Commission: 500m, Notes: null,
            Currency: "ARS",
            NewCatalogProduct: newProduct);

    private static BookingService BuildBookingService(
        AppDbContext context, IMapper mapper, bool flagOn, bool canSeeCost)
    {
        var reservaServiceMock = new Mock<IReservaService>();
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);

        var supplierServiceMock = new Mock<ISupplierService>();
        supplierServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
        var accessor = BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, Permissions.CobranzasSeeCost)
            : BuildResolver(userId);

        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableCatalogFindOrCreate = flagOn,
                StaleCostReferenceDays = 60
            });

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaServiceMock.Object,
            supplierServiceMock.Object,
            context,
            mapper,
            NullLogger<BookingService>.Instance,
            resolver,
            accessor,
            settings.Object);
    }

    private static AlertService BuildAlertService(AppDbContext context, bool deadlineAlerts)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                UpcomingUnpaidReservationAlertDays = 30,
                EnableServiceDeadlineAlerts = deadlineAlerts,
                EnableCatalogFindOrCreate = false,
                ServiceDeadlineAlertDays = 7
            });
        return new AlertService(context, settings.Object);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    // Lee un bucket del payload de /alerts por reflexion (igual que AlertServiceDeadlineBucketsTests):
    // el path OFF devuelve un objeto anonimo y el path ON un DTO; ambos se leen por nombre C#.
    private static List<object> Bucket(object payload, string key)
    {
        var value = payload.GetType().GetProperty(key)?.GetValue(payload);
        return value is IEnumerable items ? items.Cast<object>().ToList() : new List<object>();
    }

    private static T Prop<T>(object item, string name)
        => (T)item.GetType().GetProperty(name)!.GetValue(item)!;
}
