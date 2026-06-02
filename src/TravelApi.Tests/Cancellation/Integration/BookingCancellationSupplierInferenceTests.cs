using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-015 Fase 1 (2026-06-02): tests de la INFERENCIA del operador en
/// <see cref="BookingCancellationService.DraftAsync"/>. El bug previo era que la
/// inferencia solo miraba la tabla generica <c>Servicios</c> (ServicioReserva), asi
/// que las reservas armadas con servicios TIPADOS (Hotel/Vuelo/Transfer/Paquete/
/// Asistencia) no se podian cancelar.
///
/// <para>
/// Lo que esta feature garantiza y estos tests cubren:
/// <list type="bullet">
///   <item>Reserva mono-operador con servicios tipados -> se desbloquea (autorresuelve).</item>
///   <item>Reserva con varios operadores -> INV-152 (bloqueado hasta Fase 2).</item>
///   <item>No-regresion: una reserva que hoy se cancela via tabla generica con 1
///         operador sigue resolviendo EXACTAMENTE ese operador.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Por que Postgres real y no InMemory</b>: la inferencia depende del mapeo de
/// columnas legacy (la propiedad C# <c>ReservaId</c> mapea a la columna fisica
/// <c>TravelFileId</c>) y de FKs reales (cada servicio tipado exige un Supplier
/// existente). InMemory ignora esas restricciones, asi que un test verde en
/// InMemory no probaria que la query funciona contra el esquema real.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class BookingCancellationSupplierInferenceTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public BookingCancellationSupplierInferenceTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Helpers de armado
    // =========================================================================

    /// <summary>
    /// Arma el service contra Postgres real con dependencias mockeadas. Identico
    /// al patron de <see cref="BookingCancellationServiceTests"/>: no llamamos AFIP
    /// (IInvoiceService mockeado) y el flag EnableNewCancellationFlow esta ON
    /// (replica el ambiente de homologacion donde corre esta feature).
    /// </summary>
    private (BookingCancellationService service, AppDbContext ctx) BuildService()
    {
        var ctx = _fixture.CreateDbContext();

        var invoiceMock = new Mock<IInvoiceService>();
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OnePerReservaInvoicePolicy = true,
                OperatorRefundTimeoutDays = 60,
            });

        var approvalSettings = new Mock<IOperationalFinanceSettingsService>();
        approvalSettings
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        var approvalService = new ApprovalRequestService(ctx, approvalSettings.Object);

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            approvalService,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

        return (service, ctx);
    }

    /// <summary>
    /// Crea un Customer + Reserva + Invoice base (sin ningun servicio). Los tests
    /// agregan despues los servicios (genericos o tipados) segun el escenario.
    /// Devuelve el ReservaId y su PublicId (el que recibe DraftAsync).
    /// </summary>
    private static async Task<(int ReservaId, Guid ReservaPublicId)> SeedReservaWithInvoiceAsync(AppDbContext ctx)
    {
        var customer = new Customer
        {
            FullName = "Cliente Test",
            TaxCondition = "Consumidor Final",
            IsActive = true,
        };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"F-INF-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Reserva inferencia operador",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 1, // Factura A
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m,
            ReservaId = reserva.Id,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        return (reserva.Id, reserva.PublicId);
    }

    /// <summary>Crea un Supplier activo y devuelve su Id.</summary>
    private static async Task<int> SeedSupplierAsync(AppDbContext ctx, string name)
    {
        var supplier = new Supplier
        {
            Name = name,
            IsActive = true,
            TaxCondition = "IVA_RESP_INSCRIPTO",
        };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    /// <summary>Agrega un servicio en la tabla GENERICA (ServicioReserva).</summary>
    private static async Task AddGenericServiceAsync(AppDbContext ctx, int reservaId, int supplierId)
    {
        ctx.Servicios.Add(new ServicioReserva
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            ServiceType = "Generico",
            Description = "Servicio generico test",
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Agrega un HotelBooking tipado.</summary>
    private static async Task AddHotelAsync(AppDbContext ctx, int reservaId, int supplierId)
    {
        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            HotelName = "Hotel Test",
            City = "Bariloche",
            CheckIn = DateTime.UtcNow.Date,
            CheckOut = DateTime.UtcNow.Date.AddDays(3),
            Nights = 3,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Agrega un FlightSegment tipado.</summary>
    private static async Task AddFlightAsync(AppDbContext ctx, int reservaId, int supplierId)
    {
        ctx.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            AirlineCode = "AR",
            FlightNumber = "1234",
            Origin = "EZE",
            Destination = "MIA",
            DepartureTime = DateTime.UtcNow.AddDays(10),
            ArrivalTime = DateTime.UtcNow.AddDays(10).AddHours(9),
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Agrega un TransferBooking tipado.</summary>
    private static async Task AddTransferAsync(AppDbContext ctx, int reservaId, int supplierId)
    {
        ctx.TransferBookings.Add(new TransferBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            PickupLocation = "Aeropuerto EZE",
            DropoffLocation = "Hotel Centro",
            PickupDateTime = DateTime.UtcNow.AddDays(10),
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Agrega un PackageBooking tipado.</summary>
    private static async Task AddPackageAsync(AppDbContext ctx, int reservaId, int supplierId)
    {
        ctx.PackageBookings.Add(new PackageBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            PackageName = "Paquete Caribe",
            Destination = "Cancun",
            StartDate = DateTime.UtcNow.Date.AddDays(10),
            EndDate = DateTime.UtcNow.Date.AddDays(17),
            Nights = 7,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Agrega un AssistanceBooking tipado.</summary>
    private static async Task AddAssistanceAsync(AppDbContext ctx, int reservaId, int supplierId)
    {
        ctx.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            PlanType = "Plan 60",
            ValidFrom = DateTime.UtcNow.Date.AddDays(10),
            ValidTo = DateTime.UtcNow.Date.AddDays(17),
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Tipos de servicio tipado que cubre el [Theory] de autorresolucion. Cada valor
    /// se mapea a su helper Add* via <see cref="AddTypedServiceAsync"/>.
    /// </summary>
    public enum TypedService { Hotel, Flight, Transfer, Package, Assistance }

    /// <summary>Despacha al helper Add* correspondiente segun el tipo tipado.</summary>
    private static Task AddTypedServiceAsync(AppDbContext ctx, TypedService type, int reservaId, int supplierId)
        => type switch
        {
            TypedService.Hotel => AddHotelAsync(ctx, reservaId, supplierId),
            TypedService.Flight => AddFlightAsync(ctx, reservaId, supplierId),
            TypedService.Transfer => AddTransferAsync(ctx, reservaId, supplierId),
            TypedService.Package => AddPackageAsync(ctx, reservaId, supplierId),
            TypedService.Assistance => AddAssistanceAsync(ctx, reservaId, supplierId),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de servicio tipado no soportado en el test."),
        };

    private async Task<BookingCancellationDto> DraftAsync(BookingCancellationService service, Guid reservaPublicId)
        => await service.DraftAsync(
            new DraftCancellationRequest(reservaPublicId, "Cliente cambio de plan"),
            "user-vendor", "Vendedor Test", CancellationToken.None);

    // =========================================================================
    // Caso desbloqueado: reservas con servicios TIPADOS, mono-operador
    // =========================================================================

    /// <summary>
    /// Cobertura de las 5 tablas tipadas: una reserva mono-operador armada con UN
    /// servicio de cada tipo debe autorresolver ese operador. Antes del fix, las
    /// tablas distintas de la generica no se miraban y la cancelacion quedaba bloqueada.
    /// </summary>
    [Theory]
    [InlineData(TypedService.Hotel)]
    [InlineData(TypedService.Flight)]
    [InlineData(TypedService.Transfer)]
    [InlineData(TypedService.Package)]
    [InlineData(TypedService.Assistance)]
    public async Task DraftAsync_ReservaConUnServicioTipado_AutorresuelveOperador(TypedService type)
    {
        var (service, ctx) = BuildService();
        var (reservaId, reservaPublicId) = await SeedReservaWithInvoiceAsync(ctx);
        var supplierId = await SeedSupplierAsync(ctx, $"Operador {type}");
        await AddTypedServiceAsync(ctx, type, reservaId, supplierId);

        var dto = await DraftAsync(service, reservaPublicId);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(supplierId, bc.SupplierId);
    }

    [Fact]
    public async Task DraftAsync_ReservaConUnHotel_AutorresuelveOperador()
    {
        var (service, ctx) = BuildService();
        var (reservaId, reservaPublicId) = await SeedReservaWithInvoiceAsync(ctx);
        var supplierId = await SeedSupplierAsync(ctx, "Operador Hotelero");
        await AddHotelAsync(ctx, reservaId, supplierId);

        var dto = await DraftAsync(service, reservaPublicId);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(supplierId, bc.SupplierId);
    }

    [Fact]
    public async Task DraftAsync_DosHotelesMismoOperador_DedupeYAutorresuelve()
    {
        var (service, ctx) = BuildService();
        var (reservaId, reservaPublicId) = await SeedReservaWithInvoiceAsync(ctx);
        var supplierId = await SeedSupplierAsync(ctx, "Operador Unico");

        // Mismo operador en dos hoteles: el dedupe por SupplierId debe contar 1.
        await AddHotelAsync(ctx, reservaId, supplierId);
        await AddHotelAsync(ctx, reservaId, supplierId);

        var dto = await DraftAsync(service, reservaPublicId);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(supplierId, bc.SupplierId);
    }

    // =========================================================================
    // Caso bloqueado: multi-operador -> INV-152
    // =========================================================================

    [Fact]
    public async Task DraftAsync_DosHotelesOperadoresDistintos_RechazaINV152()
    {
        var (service, ctx) = BuildService();
        var (reservaId, reservaPublicId) = await SeedReservaWithInvoiceAsync(ctx);
        var supplierA = await SeedSupplierAsync(ctx, "Operador A");
        var supplierB = await SeedSupplierAsync(ctx, "Operador B");
        await AddHotelAsync(ctx, reservaId, supplierA);
        await AddHotelAsync(ctx, reservaId, supplierB);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => DraftAsync(service, reservaPublicId));
        Assert.Equal("INV-152", ex.InvariantCode);

        // Defensa en profundidad: el bloqueo NO debe dejar un BC a medias.
        Assert.False(await ctx.BookingCancellations.AnyAsync(b => b.ReservaId == reservaId));
    }

    /// <summary>
    /// Multi-operador repartido entre DOS tablas tipadas DISTINTAS (Hotel op-A +
    /// Vuelo op-B). Verifica que el dedupe junta correctamente desde fuentes
    /// distintas y que el conteo > 1 dispara INV-152.
    /// </summary>
    [Fact]
    public async Task DraftAsync_HotelYVueloOperadoresDistintos_RechazaINV152()
    {
        var (service, ctx) = BuildService();
        var (reservaId, reservaPublicId) = await SeedReservaWithInvoiceAsync(ctx);
        var supplierHotel = await SeedSupplierAsync(ctx, "Operador Hotelero");
        var supplierAerea = await SeedSupplierAsync(ctx, "Operador Aereo");
        await AddHotelAsync(ctx, reservaId, supplierHotel);
        await AddFlightAsync(ctx, reservaId, supplierAerea);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => DraftAsync(service, reservaPublicId));
        Assert.Equal("INV-152", ex.InvariantCode);

        // Defensa en profundidad: el bloqueo NO debe dejar un BC a medias.
        Assert.False(await ctx.BookingCancellations.AnyAsync(b => b.ReservaId == reservaId));
    }

    // =========================================================================
    // Cruce generica + tipada
    // =========================================================================

    [Fact]
    public async Task DraftAsync_GenericaYHotelMismoOperador_OK()
    {
        var (service, ctx) = BuildService();
        var (reservaId, reservaPublicId) = await SeedReservaWithInvoiceAsync(ctx);
        var supplierId = await SeedSupplierAsync(ctx, "Operador Compartido");
        await AddGenericServiceAsync(ctx, reservaId, supplierId);
        await AddHotelAsync(ctx, reservaId, supplierId);

        var dto = await DraftAsync(service, reservaPublicId);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(supplierId, bc.SupplierId);
    }

    [Fact]
    public async Task DraftAsync_GenericaYHotelOperadoresDistintos_RechazaINV152()
    {
        var (service, ctx) = BuildService();
        var (reservaId, reservaPublicId) = await SeedReservaWithInvoiceAsync(ctx);
        var supplierGenerico = await SeedSupplierAsync(ctx, "Operador Generico");
        var supplierHotel = await SeedSupplierAsync(ctx, "Operador Hotel");

        // Caso documentado del cambio de comportamiento: antes la inferencia tomaba
        // SOLO el operador generico (cancelaba con el operador equivocado, ignorando
        // el hotel). Ahora detecta los dos operadores y bloquea con INV-152.
        await AddGenericServiceAsync(ctx, reservaId, supplierGenerico);
        await AddHotelAsync(ctx, reservaId, supplierHotel);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => DraftAsync(service, reservaPublicId));
        Assert.Equal("INV-152", ex.InvariantCode);
    }

    // =========================================================================
    // No-regresion del path generico + caso sin operador
    // =========================================================================

    [Fact]
    public async Task DraftAsync_SoloGenericaUnOperador_NoRegresion_MismoOperador()
    {
        var (service, ctx) = BuildService();
        var (reservaId, reservaPublicId) = await SeedReservaWithInvoiceAsync(ctx);
        var supplierId = await SeedSupplierAsync(ctx, "Operador Generico Legacy");

        // Path que funcionaba antes del cambio: reserva con servicio generico unico.
        await AddGenericServiceAsync(ctx, reservaId, supplierId);

        var dto = await DraftAsync(service, reservaPublicId);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == dto.PublicId);
        Assert.Equal(supplierId, bc.SupplierId);
    }

    [Fact]
    public async Task DraftAsync_SinNingunOperador_RechazaConErrorActual()
    {
        var (service, ctx) = BuildService();
        var (_, reservaPublicId) = await SeedReservaWithInvoiceAsync(ctx);
        // No agregamos ningun servicio (ni generico ni tipado).

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DraftAsync(service, reservaPublicId));
        Assert.Contains("no tiene servicios con Supplier asignado", ex.Message);
    }
}
