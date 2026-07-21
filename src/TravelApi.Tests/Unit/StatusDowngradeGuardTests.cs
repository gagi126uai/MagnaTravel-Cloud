using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
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
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// P1 "circuito proveedor" (2026-07-21): candado UNIFICADO de "bajar el estado" de un servicio (de
/// Confirmado a Solicitado/Cancelado) con el candado R1 de "anular servicio". Antes de esta tanda,
/// "bajar el estado" bloqueaba mirando el TOTAL de pagos de TODA la reserva y nunca miraba si ya
/// habia factura viva; el mensaje pedia lo CONTRARIO que "anular servicio" para el MISMO riesgo de
/// plata (hallazgo de Gaston, docs/architecture/2026-07-21-circuito-proveedor-inventario.md).
///
/// <para>Los tests ejercitan <c>BookingService.UpdateHotelStatusAsync</c> REAL (el PATCH liviano que
/// usa la cuenta corriente del proveedor), con un <see cref="BookingCancellationService"/> real
/// compartiendo el mismo contexto — mismo patron que <c>OperatorReassignmentGuardTests</c>.</para>
/// </summary>
public class StatusDowngradeGuardTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"status-downgrade-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static readonly IMapper Mapper =
        new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

    private static IHttpContextAccessor AdminContext()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin-1"),
            new(ClaimTypes.Role, "Admin"),
        };
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

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

    private static IReservaService BuildReservaServiceMock()
    {
        var mock = new Mock<IReservaService>();
        mock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        mock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        return mock.Object;
    }

    /// <summary>Mock de ISupplierService cuyo UpdateBalanceAsync corre el persister REAL (como en produccion).</summary>
    private static ISupplierService BuildSupplierServiceBackedByPersister(AppDbContext ctx)
    {
        var mock = new Mock<ISupplierService>();
        mock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (int supplierId, CancellationToken c) =>
            {
                await SupplierDebtPersister.PersistAsync(ctx, supplierId, c);
                await ctx.SaveChangesAsync(c);
            });
        return mock.Object;
    }

    private static BookingService BuildBookingService(AppDbContext ctx, IBookingCancellationService? cancellationService) =>
        new(
            new Repository<FlightSegment>(ctx),
            new Repository<HotelBooking>(ctx),
            new Repository<PackageBooking>(ctx),
            new Repository<TransferBooking>(ctx),
            new Repository<AssistanceBooking>(ctx),
            new Repository<Reserva>(ctx),
            new Repository<Supplier>(ctx),
            BuildReservaServiceMock(),
            BuildSupplierServiceBackedByPersister(ctx),
            ctx,
            Mapper,
            NullLogger<BookingService>.Instance,
            permissionResolver: null,
            httpContextAccessor: AdminContext(),
            settingsService: null,
            auditService: null,
            cancellationService: cancellationService);

    // ---------- seeds ----------

    private static async Task<(Reserva Reserva, Supplier Operator, HotelBooking Hotel)> SeedConfirmedPaidHotelAsync(
        AppDbContext ctx, bool withLiveInvoice, bool withPayment)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador Test", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-DOWNGRADE-1", Name = "Reserva downgrade", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = "Hotel Test", City = "Bariloche", Country = "Argentina",
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
    // (1) BLOQUEA: Confirmado + pagado + SIN factura -> bajar a Solicitado rechaza con el MISMO code y
    //     mensaje que "anular servicio" (R1), adaptado a "cambiar el estado".
    // ============================================================
    [Fact]
    public async Task DowngradeToSolicitado_ConfirmedPaidHotel_NoInvoice_Blocks_WithUnifiedCodeAndMessage()
    {
        await using var ctx = NewContext();
        var (_, _, hotel) = await SeedConfirmedPaidHotelAsync(ctx, withLiveInvoice: false, withPayment: true);
        var booking = BuildBookingService(ctx, BuildCancellationService(ctx));

        var ex = await Assert.ThrowsAsync<ServiceCancellationRejectedException>(() =>
            booking.UpdateHotelStatusAsync(hotel.Id.ToString(), "Solicitado", confirmationNumber: null, CancellationToken.None));

        Assert.Equal(ServiceCancellationRejectedException.Codes.UnanchoredOperatorRefund, ex.Code);
        Assert.Equal(ServiceCancellationPreflightPolicy.UnanchoredOperatorRefundBlockedReasonForStatusDowngrade, ex.Message);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Confirmado", reloaded.Status); // no muto
    }

    // ============================================================
    // (2) ESCAPE (D1): Confirmado + pagado + CON factura viva -> bajar el estado se PERMITE (mismo
    //     ancla que "anular servicio").
    // ============================================================
    [Fact]
    public async Task DowngradeToSolicitado_ConfirmedPaidHotel_WithLiveInvoice_Allows()
    {
        await using var ctx = NewContext();
        var (_, _, hotel) = await SeedConfirmedPaidHotelAsync(ctx, withLiveInvoice: true, withPayment: true);
        var booking = BuildBookingService(ctx, BuildCancellationService(ctx));

        await booking.UpdateHotelStatusAsync(hotel.Id.ToString(), "Solicitado", confirmationNumber: null, CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Solicitado", reloaded.Status);
    }

    // ============================================================
    // (3) IMPAGO: Confirmado SIN pagos -> bajar el estado se PERMITE (nunca hubo plata para anclar).
    // ============================================================
    [Fact]
    public async Task DowngradeToSolicitado_ConfirmedUnpaidHotel_Allows()
    {
        await using var ctx = NewContext();
        var (_, _, hotel) = await SeedConfirmedPaidHotelAsync(ctx, withLiveInvoice: false, withPayment: false);
        var booking = BuildBookingService(ctx, BuildCancellationService(ctx));

        await booking.UpdateHotelStatusAsync(hotel.Id.ToString(), "Cancelado", confirmationNumber: null, CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Cancelado", reloaded.Status);
    }

    // ============================================================
    // (4) NO ES UNA BAJADA: Cancelado -> Solicitado (ninguno de los dos es "Confirmado") nunca corre
    //     el candado de plata, ni siquiera cuando hay pagos en la reserva — el gate barato
    //     (IsStatusDowngradeFromConfirmed) corta ANTES de tocar el IBookingCancellationService. Lo
    //     probamos con un mock que explota si se lo llama, para probar el short-circuit (no solo que
    //     "no bloquea"). Se elige esta transicion (en vez de Solicitado -&gt; Confirmado) para no cruzarse
    //     con el candado NO RELACIONADO de cobertura nominal de pasajeros (ADR-031), que exige titular
    //     cuando un servicio pasa a estar RESUELTO — ruido ajeno a lo que este test quiere probar.
    // ============================================================
    [Fact]
    public async Task NonDowngradeTransition_NeverCallsTheDowngradeGuard()
    {
        await using var ctx = NewContext();
        var (reserva, supplier, _) = await SeedConfirmedPaidHotelAsync(ctx, withLiveInvoice: false, withPayment: true);

        // Segundo hotel, arrancando en "Cancelado" -> lo pasamos a "Solicitado". Ninguno de los dos
        // estados es "Confirmado", asi que wasConfirmed=false -> nunca es una bajada.
        var hotel2 = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Cancelado",
            HotelName = "Hotel 2", City = "Ushuaia", Country = "Argentina",
            NetCost = 20_000m, SalePrice = 30_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(15), CheckOut = DateTime.UtcNow.Date.AddDays(17),
        };
        ctx.HotelBookings.Add(hotel2);
        await ctx.SaveChangesAsync();

        var explodingCancellationService = new Mock<IBookingCancellationService>(MockBehavior.Strict);
        // A proposito: NINGUN Setup -> si BookingService llegara a invocar cualquier metodo de esta
        // interfaz, Moq (Strict) tira MockException. Confirma que una transicion que NO es bajada ni
        // siquiera pregunta por el candado de plata.
        var booking = BuildBookingService(ctx, explodingCancellationService.Object);

        await booking.UpdateHotelStatusAsync(hotel2.Id.ToString(), "Solicitado", confirmationNumber: null, CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel2.Id);
        Assert.Equal("Solicitado", reloaded.Status);
    }

    // ============================================================
    // (5) CTOR VIEJO (_cancellationService null): no corre el candado (mismo patron ya aceptado para
    //     GuardOperatorOrCurrencyReassignmentAsync — en produccion la DI siempre lo inyecta).
    // ============================================================
    [Fact]
    public async Task DowngradeToSolicitado_WithoutCancellationServiceWired_DoesNotBlock()
    {
        await using var ctx = NewContext();
        var (_, _, hotel) = await SeedConfirmedPaidHotelAsync(ctx, withLiveInvoice: false, withPayment: true);
        var booking = BuildBookingService(ctx, cancellationService: null);

        await booking.UpdateHotelStatusAsync(hotel.Id.ToString(), "Solicitado", confirmationNumber: null, CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Solicitado", reloaded.Status);
    }
}
