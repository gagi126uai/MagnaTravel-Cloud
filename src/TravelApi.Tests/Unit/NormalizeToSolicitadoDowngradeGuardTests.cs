using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda P4 "circuito proveedor" (2026-07-22): el candado R1 (plata pagada al operador sin factura de
/// venta que ancle el reembolso) tambien tiene que correr cuando <c>ReservaService</c> normaliza TODOS
/// los servicios de una reserva a "Solicitado" al pasar de Presupuesto a En gestion
/// (<c>EnsureReadinessForSaleAsync</c> -&gt; <c>NormalizeAllServicesToSolicitadoAsync</c>). Antes de esta
/// tanda ese metodo bajaba el estado de CUALQUIER servicio (incluido uno "Confirmado" con plata pagada
/// al operador) sin pasar por el mismo candado que ya protege a <c>BookingService</c> en sus 11 sitios
/// (<see cref="StatusDowngradeGuardTests"/>) — mismo riesgo de plata, gap distinto.
///
/// <para>Los tests ejercitan <c>ReservaService.UpdateStatusAsync</c> REAL (transicion manual Presupuesto
/// -&gt; En gestion) con un <see cref="BookingCancellationService"/> real compartiendo el mismo contexto,
/// mismo patron que <see cref="StatusDowngradeGuardTests"/>.</para>
/// </summary>
public class NormalizeToSolicitadoDowngradeGuardTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"normalize-solicitado-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static readonly IMapper Mapper = new Mock<IMapper>().Object;

    private static IOperationalFinanceSettingsService SettingsService()
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true });
        return mock.Object;
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    /// <summary>BookingCancellationService REAL compartiendo el MISMO contexto (como en DI scoped).</summary>
    private static IBookingCancellationService BuildCancellationService(AppDbContext ctx) =>
        new BookingCancellationService(
            ctx, new Mock<IInvoiceService>().Object, new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object, NullLogger<BookingCancellationService>.Instance,
            SettingsService(), new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

    private static ReservaService BuildReservaService(AppDbContext ctx, IBookingCancellationService? cancellationService) =>
        new(
            ctx,
            Mapper,
            SettingsService(),
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null,
            autoStateService: null,
            auditService: null,
            cancellationService: cancellationService);

    /// <summary>
    /// Siembra una reserva en Presupuesto (1 pasajero declarado, lista para "cliente aceptó") con UN
    /// hotel "Confirmado" de un operador. Opcionalmente le carga un pago al operador y/o una factura de
    /// venta viva, replicando el bypass de API / data preexistente que el metodo normalizador defiende.
    /// </summary>
    private static async Task<(Reserva Reserva, Supplier Operador, HotelBooking Hotel)> SeedBudgetReservaWithConfirmedHotelAsync(
        AppDbContext ctx, bool withPayment, bool withLiveInvoice, string hotelName = "Hotel Test")
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador Test", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-NORMALIZE-1", Name = "Reserva normalizar", Status = EstadoReserva.Budget,
            PayerId = customer.Id, Balance = 0m, AdultCount = 1, ChildCount = 0, InfantCount = 0,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = hotelName, City = "Bariloche", Country = "Argentina",
            NetCost = 50_000m, SalePrice = 75_000m, Currency = "ARS",
            ConfirmedAt = DateTime.UtcNow.AddDays(-1),
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12),
        };
        ctx.HotelBookings.Add(hotel);

        if (withPayment)
        {
            ctx.SupplierPayments.Add(new SupplierPayment
            {
                SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 50_000m, Currency = "ARS",
                Method = "Transferencia",
            });
        }

        if (withLiveInvoice)
        {
            ctx.Invoices.Add(new Invoice
            {
                ReservaId = reserva.Id, TipoComprobante = 1, CAE = "12345678901234",
                AnnulmentStatus = AnnulmentStatus.None,
            });
        }

        await ctx.SaveChangesAsync();
        if (withPayment)
        {
            await SupplierDebtPersister.PersistAsync(ctx, supplier.Id);
            await ctx.SaveChangesAsync();
        }

        return (reserva, supplier, hotel);
    }

    // ============================================================
    // (1) BLOQUEA: hotel Confirmado + pagado + SIN factura -> "cliente aceptó" (Presupuesto -> En
    //     gestion) rechaza TODA la operacion con el mismo Code que "anular servicio"/"bajar el estado",
    //     y el mensaje nombra el hotel puntual que frena.
    // ============================================================
    [Fact]
    public async Task NormalizeToSolicitado_ConfirmedPaidHotel_NoInvoice_Blocks_AndNamesTheService()
    {
        await using var ctx = NewContext();
        var (reserva, _, hotel) = await SeedBudgetReservaWithConfirmedHotelAsync(
            ctx, withPayment: true, withLiveInvoice: false, hotelName: "Sheraton Bariloche");
        var service = BuildReservaService(ctx, BuildCancellationService(ctx));

        var ex = await Assert.ThrowsAsync<ServiceCancellationRejectedException>(
            () => service.UpdateStatusAsync(reserva.Id, EstadoReserva.InManagement));

        Assert.Equal(ServiceCancellationRejectedException.Codes.UnanchoredOperatorRefund, ex.Code);
        // Obra "anular sin factura" (2026-07-23): el Code queda igual (T-1, estable); el TEXTO se alineó al
        // mismo estilo que los otros dos candados de la familia que siguen bloqueando — ya no pide "emitir
        // factura" (dejó de ser requisito). T-6: texto exacto fijado acá.
        Assert.Equal(
            "No se puede completar esta acción: el hotel 'Sheraton Bariloche' ya tiene pagos al operador " +
            "que todavía no están resueltos. Gestioná primero el reembolso con el operador (o cancelá el " +
            "servicio) antes de continuar.",
            ex.Message);

        // Nada mutó: ni el servicio bajó de estado ni la reserva avanzó (rechazo atómico).
        var reloadedHotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Confirmado", reloadedHotel.Status);
        var reloadedReserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Budget, reloadedReserva.Status);
    }

    // ============================================================
    // (2) ATOMICO: con DOS servicios (uno bloqueado, otro que solo por si mismo pasaria), el rechazo
    //     de UNO frena a los DOS — nada de bajar la mitad.
    // ============================================================
    [Fact]
    public async Task NormalizeToSolicitado_OneBlockedServiceAmongTwo_RejectsBoth_NoPartialDowngrade()
    {
        await using var ctx = NewContext();
        var (reserva, supplier, blockedHotel) = await SeedBudgetReservaWithConfirmedHotelAsync(
            ctx, withPayment: true, withLiveInvoice: false, hotelName: "Hotel Bloqueado");

        // Segundo hotel, Confirmado pero SIN pagos: si se evaluara solo, no bloquearia.
        var freeHotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = "Hotel Libre", City = "Ushuaia", Country = "Argentina",
            NetCost = 10_000m, SalePrice = 15_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(20), CheckOut = DateTime.UtcNow.Date.AddDays(22),
        };
        ctx.HotelBookings.Add(freeHotel);
        await ctx.SaveChangesAsync();

        var service = BuildReservaService(ctx, BuildCancellationService(ctx));

        await Assert.ThrowsAsync<ServiceCancellationRejectedException>(
            () => service.UpdateStatusAsync(reserva.Id, EstadoReserva.InManagement));

        var reloadedBlocked = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == blockedHotel.Id);
        var reloadedFree = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == freeHotel.Id);
        Assert.Equal("Confirmado", reloadedBlocked.Status);
        Assert.Equal("Confirmado", reloadedFree.Status); // tampoco bajo, aunque el solo hubiera pasado
    }

    // ============================================================
    // (3) ESCAPE: hotel Confirmado + pagado + CON factura viva -> normaliza sin problema (mismo ancla
    //     que "anular servicio"/"bajar el estado").
    // ============================================================
    [Fact]
    public async Task NormalizeToSolicitado_ConfirmedPaidHotel_WithLiveInvoice_Allows()
    {
        await using var ctx = NewContext();
        var (reserva, _, hotel) = await SeedBudgetReservaWithConfirmedHotelAsync(
            ctx, withPayment: true, withLiveInvoice: true);
        var service = BuildReservaService(ctx, BuildCancellationService(ctx));

        var result = await service.UpdateStatusAsync(reserva.Id, EstadoReserva.InManagement);

        Assert.Equal(EstadoReserva.InManagement, result.Status);
        var reloadedHotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Solicitado", reloadedHotel.Status);
    }

    // ============================================================
    // (4) IMPAGO: hotel Confirmado SIN pagos -> normaliza sin problema (nunca hubo plata para anclar).
    // ============================================================
    [Fact]
    public async Task NormalizeToSolicitado_ConfirmedUnpaidHotel_Allows()
    {
        await using var ctx = NewContext();
        var (reserva, _, hotel) = await SeedBudgetReservaWithConfirmedHotelAsync(
            ctx, withPayment: false, withLiveInvoice: false);
        var service = BuildReservaService(ctx, BuildCancellationService(ctx));

        var result = await service.UpdateStatusAsync(reserva.Id, EstadoReserva.InManagement);

        Assert.Equal(EstadoReserva.InManagement, result.Status);
        var reloadedHotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Solicitado", reloadedHotel.Status);
    }

    // ============================================================
    // (5) CTOR SIN _cancellationService: no corre el candado (mismo patron ya aceptado para
    //     GuardStatusDowngradeAsync en BookingService — en produccion la DI siempre lo inyecta).
    // ============================================================
    [Fact]
    public async Task NormalizeToSolicitado_WithoutCancellationServiceWired_DoesNotBlock()
    {
        await using var ctx = NewContext();
        var (reserva, _, hotel) = await SeedBudgetReservaWithConfirmedHotelAsync(
            ctx, withPayment: true, withLiveInvoice: false);
        var service = BuildReservaService(ctx, cancellationService: null);

        var result = await service.UpdateStatusAsync(reserva.Id, EstadoReserva.InManagement);

        Assert.Equal(EstadoReserva.InManagement, result.Status);
        var reloadedHotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Solicitado", reloadedHotel.Status);
    }

    // ============================================================
    // (6) CONTRATO HTTP: el PUT /reservas/{id}/status del controller devuelve { message, code } (envelope
    //     ADITIVO, mismo patron que HotelBookingsController/TransferBookingsController/etc.) cuando el
    //     rechazo es el candado R1 — asi el frontend puede mapear el Code a un boton en vez de adivinar
    //     por el texto. El status HTTP sigue siendo 400 (BadRequest): este endpoint ya usaba 400 para
    //     TODO InvalidOperationException antes de esta tanda, y esta tanda no cambia ese contrato.
    // ============================================================
    [Fact]
    public async Task ReservasController_UpdateStatus_MapsServiceCancellationRejectedException_ToMessageAndCode()
    {
        var reservaServiceMock = new Mock<IReservaService>();
        reservaServiceMock
            .Setup(s => s.UpdateStatusAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceCancellationRejectedException(
                ServiceCancellationRejectedException.Codes.UnanchoredOperatorRefund,
                "No se puede completar esta acción: el hotel 'Sheraton Bariloche' ya tiene pagos al operador..."));

        var controller = new ReservasController(
            reservaServiceMock.Object,
            Mock.Of<IVoucherService>(),
            Mock.Of<ITimelineService>(),
            Mock.Of<ISupplierService>(),
            Mock.Of<IEntityReferenceResolver>(),
            Mock.Of<IBookingService>(),
            NullLogger<TravelApi.Controllers.ReservasController>.Instance);

        // El metodo lee User.FindFirstValue(ClaimTypes.NameIdentifier) para el audit del actor: sin un
        // HttpContext con ClaimsPrincipal, esa lectura tira ANTES de llegar a _reservaService, y el catch
        // de ArgumentException (mas generico) se comeria el escenario que este test quiere probar.
        var httpContext = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(new[]
                {
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "admin-1"),
                }, "Test")),
        };
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = httpContext,
        };

        var result = await controller.UpdateStatus(
            "reserva-1", new StatusUpdateRequest(EstadoReserva.InManagement), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);

        // El body es un objeto anonimo { message, code }: lo leemos por reflexion, mismo patron que ya
        // usan los tests de contrato HTTP de esta familia (ver PaymentServiceDeleteTests).
        var body = badRequest.Value!;
        var messageProp = body.GetType().GetProperty("message");
        var codeProp = body.GetType().GetProperty("code");
        Assert.NotNull(messageProp);
        Assert.NotNull(codeProp);
        Assert.Contains("Sheraton Bariloche", (string)messageProp!.GetValue(body)!);
        Assert.Equal(ServiceCancellationRejectedException.Codes.UnanchoredOperatorRefund, (string)codeProp!.GetValue(body)!);
    }
}
