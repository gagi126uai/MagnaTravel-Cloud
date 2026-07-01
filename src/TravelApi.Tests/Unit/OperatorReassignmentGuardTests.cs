using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Candado de plata viva (familia R1, 2026-07-01) contra la "cuarta fuga": REASIGNAR el operador (o cambiar la
/// moneda) de un servicio confirmado y PAGADO al operador, en una reserva SIN factura viva, hace desaparecer su
/// compra confirmada del bucket del operador saliente y no crea ninguna <c>BookingCancellationLine</c> que ancle el
/// receivable -> el <see cref="SupplierCreditReconciler"/> materializaria el negativo de caja como saldo a favor
/// GASTABLE (fuga).
///
/// <para><b>Criterio (unificado con R1-parcial y anular-total)</b>: el candado reusa el mismo núcleo
/// <c>ComputeUnanchoredOperatorRefundCapAsync</c> (scope parcial, filtrado al servicio). El pool de lo pagado al
/// operador se arma con <c>SupplierPayments.Where(ReservaId == reservaId)</c>, así que EXCLUYE el prepago "a cuenta"
/// (pagos con <c>ReservaId == null</c>): un saldo a favor on-account del operador NO dispara el candado (fix del
/// over-block que motivó esta versión). Solo bloquea cuando hay plata IMPUTADA A ESTA RESERVA por el servicio que
/// quedaría sin ancla.</para>
///
/// <para>Los tests ejercitan los <c>Update*Async</c> REALES (BookingService tipado + ReservaService genérico), que
/// delegan en un <see cref="BookingCancellationService"/> real que comparte el mismo contexto. La variante por
/// CAMBIO DE MONEDA se cubre llamando al método del service directamente, porque los requests de edición no exponen
/// la moneda del servicio hoy.</para>
/// </summary>
public class OperatorReassignmentGuardTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"reassign-{Guid.NewGuid()}")
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
        mock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(),
                It.IsAny<TravelApi.Application.Contracts.Reservations.PendingServiceChange?>()))
            .Returns(Task.CompletedTask);
        return mock.Object;
    }

    /// <summary>Mock de ISupplierService cuyo UpdateBalanceAsync corre el persister REAL (como en producción).</summary>
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

    private static IUserPermissionResolver SeeCostResolver()
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string> { Permissions.CobranzasSeeCost };
        mock.Setup(r => r.GetPermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static BookingService BuildBookingService(AppDbContext ctx) =>
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
            SeeCostResolver(),
            AdminContext(),
            settingsService: null,
            auditService: null,
            cancellationService: BuildCancellationService(ctx));

    private static ReservaService BuildReservaService(AppDbContext ctx) =>
        new(ctx, Mapper, SettingsService(), BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: AdminContext(),
            autoStateService: null, auditService: null, cancellationService: BuildCancellationService(ctx));

    // ---------- seeds ----------

    private static async Task<(Reserva Reserva, Supplier OperatorA, Supplier OperatorB)> SeedReservaWithTwoOperatorsAsync(
        AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var operatorA = new Supplier { Name = "Operador A", IsActive = true };
        var operatorB = new Supplier { Name = "Operador B", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.AddRange(operatorA, operatorB);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-REASSIGN-1", Name = "Reserva test", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return (reserva, operatorA, operatorB);
    }

    private static HotelBooking AddConfirmedHotel(AppDbContext ctx, int reservaId, int supplierId, decimal netCost, string currency = "ARS")
    {
        var hotel = new HotelBooking
        {
            ReservaId = reservaId, SupplierId = supplierId, Status = "Confirmado",
            HotelName = "Hotel Test", City = "Bariloche", Country = "Argentina",
            NetCost = netCost, SalePrice = netCost * 1.5m, Currency = currency,
            ConfirmedAt = DateTime.UtcNow.AddDays(-1),
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12),
        };
        ctx.HotelBookings.Add(hotel);
        return hotel;
    }

    /// <summary>Pago al operador imputado a ESTA reserva (entra al pool del RefundCap).</summary>
    private static void AddSupplierPaymentImputedToReserva(AppDbContext ctx, int supplierId, int reservaId, decimal amount, string currency = "ARS")
        => ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierId, ReservaId = reservaId, Amount = amount, Currency = currency, Method = "Transferencia",
        });

    /// <summary>Prepago "a cuenta" del operador (ReservaId null): NO entra al pool del RefundCap.</summary>
    private static void AddSupplierPaymentOnAccount(AppDbContext ctx, int supplierId, decimal amount, string currency = "ARS")
        => ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierId, ReservaId = null, Amount = amount, Currency = currency, Method = "Transferencia",
        });

    private static async Task PersistSupplierBalanceAsync(AppDbContext ctx, int supplierId)
    {
        await SupplierDebtPersister.PersistAsync(ctx, supplierId);
        await ctx.SaveChangesAsync();
    }

    private static UpdateHotelRequest BuildHotelUpdate(string supplierPublicId, decimal netCost, decimal? checkInOffsetDays = null)
        => new(
            SupplierId: supplierPublicId, HotelName: "Hotel Test", StarRating: 4, City: "Bariloche", Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays((double)(checkInOffsetDays ?? 10)),
            CheckOut: DateTime.UtcNow.Date.AddDays((double)(checkInOffsetDays ?? 10) + 2),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: netCost, SalePrice: netCost * 1.5m, Commission: netCost * 0.5m, Status: "Confirmado", Notes: null,
            RoomingAssignments: null, RateId: null, WorkflowStatus: "Confirmado");

    private static async Task<decimal> PoolAsync(AppDbContext ctx, int supplierId) =>
        (await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync())
            .Sum(e => e.RemainingBalance);

    // ============================================================
    // (1) FUGA (hotel): cambiar operador de un servicio confirmado + PAGADO IMPUTADO A ESTA RESERVA + sin factura
    //     -> BLOQUEA y no muta.
    // ============================================================
    [Fact]
    public async Task ChangeOperator_ConfirmedPaidHotel_ImputedToReserva_NoInvoice_Blocks_AndDoesNotMutate()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, operatorB) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var booking = BuildBookingService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            booking.UpdateHotelAsync(reserva.Id, hotel.Id, BuildHotelUpdate(operatorB.PublicId.ToString(), 50_000m), CancellationToken.None));
        Assert.Contains("cambiar el operador", ex.Message);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(operatorA.Id, reloaded.SupplierId); // no muto

        await SupplierCreditReconciler.ReconcileAsync(ctx, operatorA.Id, null, null, null, null, CancellationToken.None);
        Assert.Equal(0m, await PoolAsync(ctx, operatorA.Id));
    }

    // ============================================================
    // (2) IMPAGO (hotel): confirmado pero SIN pagos -> NO bloquea (edicion normal).
    // ============================================================
    [Fact]
    public async Task ChangeOperator_ConfirmedUnpaidHotel_DoesNotBlock()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, operatorB) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var booking = BuildBookingService(ctx);

        await booking.UpdateHotelAsync(reserva.Id, hotel.Id, BuildHotelUpdate(operatorB.PublicId.ToString(), 50_000m), CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(operatorB.Id, reloaded.SupplierId);
    }

    // ============================================================
    // (3) FACTURA VIVA: lo frena AGUAS ARRIBA MutationGuards (candado post-CAE), no esta guarda.
    // ============================================================
    [Fact]
    public async Task ChangeOperator_WithLiveInvoice_BlockedByFiscalGuard_Upstream()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, operatorB) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m);
        ctx.Invoices.Add(new Invoice
        {
            ReservaId = reserva.Id, TipoComprobante = 1, CAE = "12345678901234", AnnulmentStatus = AnnulmentStatus.None,
        });
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var booking = BuildBookingService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            booking.UpdateHotelAsync(reserva.Id, hotel.Id, BuildHotelUpdate(operatorB.PublicId.ToString(), 50_000m), CancellationToken.None));
        Assert.Contains("factura emitida", ex.Message); // mensaje fiscal, no el de reasignacion

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(operatorA.Id, reloaded.SupplierId);
    }

    // ============================================================
    // (4) FIX DEL OVER-BLOCK (el que motivó esta versión): el operador tiene un PREPAGO A CUENTA (SupplierPayment con
    //     ReservaId null) que deja su caja GLOBAL negativa, pero NO hay pago imputado a esta reserva por el servicio.
    //     Reasignar el operador de un servicio confirmado -> AHORA **NO** bloquea (antes, con el criterio de caja
    //     global, bloqueaba de más). El pool del RefundCap excluye el pago on-account.
    // ============================================================
    [Fact]
    public async Task ChangeOperator_OperatorHasOnAccountPrepago_NoImputedPayment_DoesNotBlock()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, operatorB) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        // Prepago a cuenta del operador A (no imputado a ninguna reserva): su caja global queda negativa, pero esto
        // NO es plata colgada de este servicio.
        AddSupplierPaymentOnAccount(ctx, operatorA.Id, amount: 80_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var booking = BuildBookingService(ctx);

        // No bloquea: el pool imputado a la reserva es 0 -> RefundCap 0.
        await booking.UpdateHotelAsync(reserva.Id, hotel.Id, BuildHotelUpdate(operatorB.PublicId.ToString(), 50_000m), CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(operatorB.Id, reloaded.SupplierId);
    }

    // ============================================================
    // (4-bis) NO UNDER-BLOCK: el operador tiene prepago a cuenta Y ADEMÁS un pago imputado a esta reserva por el
    //     servicio. El on-account se ignora pero el imputado SÍ dispara el bloqueo (la fuga real se sigue tapando).
    // ============================================================
    [Fact]
    public async Task ChangeOperator_OnAccountPlusImputedPayment_StillBlocks()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, operatorB) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentOnAccount(ctx, operatorA.Id, amount: 30_000m);                          // se ignora
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m);       // dispara el bloqueo
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var booking = BuildBookingService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            booking.UpdateHotelAsync(reserva.Id, hotel.Id, BuildHotelUpdate(operatorB.PublicId.ToString(), 50_000m), CancellationToken.None));

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(operatorA.Id, reloaded.SupplierId);
    }

    // ============================================================
    // (5) BAJAR NETCOST con el MISMO operador y servicio vivo -> NO es esta fuga (sobrepago legítimo ADR-041).
    // ============================================================
    [Fact]
    public async Task LowerNetCost_SameOperator_LiveService_DoesNotBlock()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, _) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var booking = BuildBookingService(ctx);

        await booking.UpdateHotelAsync(reserva.Id, hotel.Id, BuildHotelUpdate(operatorA.PublicId.ToString(), 30_000m), CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(operatorA.Id, reloaded.SupplierId);
        Assert.Equal(30_000m, reloaded.NetCost);
    }

    // ============================================================
    // (6) EDICION que NO toca operador ni moneda (solo fechas) sobre un servicio confirmado + pagado -> NO bloquea.
    // ============================================================
    [Fact]
    public async Task EditDatesOnly_SameOperator_ConfirmedPaid_DoesNotBlock()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, _) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var booking = BuildBookingService(ctx);

        await booking.UpdateHotelAsync(reserva.Id, hotel.Id,
            BuildHotelUpdate(operatorA.PublicId.ToString(), 50_000m, checkInOffsetDays: 30), CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(operatorA.Id, reloaded.SupplierId);
    }

    // ============================================================
    // (7) FUGA (servicio genérico): cambiar operador confirmado + pagado imputado + sin factura -> BLOQUEA.
    // ============================================================
    [Fact]
    public async Task ChangeOperator_ConfirmedPaidGenericService_ImputedToReserva_NoInvoice_Blocks()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, operatorB) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var service = new ServicioReserva
        {
            ReservaId = reserva.Id, ServiceType = "Excursion", ProductType = "Excursion", Description = "City tour",
            ConfirmationNumber = "ABC", Status = "Confirmado", Currency = "ARS",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 75_000m, NetCost = 50_000m, Commission = 25_000m,
            SupplierId = operatorA.Id, ConfirmedAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow,
        };
        ctx.Servicios.Add(service);
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var reservaService = BuildReservaService(ctx);

        var request = new AddServiceRequest(
            ServiceType: "Excursion", SupplierId: operatorB.PublicId.ToString(), Description: "City tour",
            ConfirmationNumber: "ABC", DepartureDate: DateTime.UtcNow.AddDays(15), ReturnDate: null,
            SalePrice: 75_000m, NetCost: 50_000m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reservaService.UpdateServiceAsync(service.Id, request, CancellationToken.None));
        Assert.Contains("cambiar el operador", ex.Message);

        var reloaded = await ctx.Servicios.AsNoTracking().FirstAsync(s => s.Id == service.Id);
        Assert.Equal(operatorA.Id, reloaded.SupplierId);
    }

    // ============================================================
    // (8) FUGA (aéreo — un typed distinto de hotel, N6): cambiar operador de un vuelo confirmado (HK) + pagado
    //     imputado + sin factura -> BLOQUEA y no muta.
    // ============================================================
    [Fact]
    public async Task ChangeOperator_ConfirmedPaidFlight_ImputedToReserva_Blocks_AndDoesNotMutate()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, operatorB) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var flight = new FlightSegment
        {
            ReservaId = reserva.Id, SupplierId = operatorA.Id, Status = "HK", Currency = "ARS",
            AirlineCode = "AR", FlightNumber = "1234", NetCost = 50_000m, SalePrice = 75_000m,
            DepartureTime = DateTime.UtcNow.AddDays(20), ArrivalTime = DateTime.UtcNow.AddDays(20).AddHours(3),
        };
        ctx.FlightSegments.Add(flight);
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var booking = BuildBookingService(ctx);

        var request = new UpdateFlightRequest(
            SupplierId: operatorB.PublicId.ToString(), AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "MAD", DestinationCity: "Madrid",
            DepartureTime: DateTime.UtcNow.AddDays(20), ArrivalTime: DateTime.UtcNow.AddDays(20).AddHours(3),
            CabinClass: null, Baggage: null, TicketNumber: null, PNR: null,
            NetCost: 50_000m, SalePrice: 75_000m, Commission: 25_000m, Tax: 0m, Status: "Confirmado", Notes: null,
            RateId: null, WorkflowStatus: "Confirmado");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            booking.UpdateFlightAsync(reserva.Id, flight.Id, request, CancellationToken.None));
        Assert.Contains("cambiar el operador", ex.Message);

        var reloaded = await ctx.FlightSegments.AsNoTracking().FirstAsync(f => f.Id == flight.Id);
        Assert.Equal(operatorA.Id, reloaded.SupplierId);
    }

    // ============================================================
    // (9) MULTI-SERVICIO mismo operador, pago parcial imputado: criterio CONSERVADOR unificado con R1-parcial. El pool
    //     imputado (50.000) topeado por el costo del servicio movido (50.000) da RefundCap > 0 -> BLOQUEA. Es el mismo
    //     comportamiento que ya tiene cancelar ese servicio suelto (no se puede atribuir el pago por servicio).
    // ============================================================
    [Fact]
    public async Task ChangeOperator_TwoHotelsSameOperator_PartiallyPaidImputed_Blocks_ConservativeUnified()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, operatorB) = await SeedReservaWithTwoOperatorsAsync(ctx);
        AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);              // hotel 1 (se queda con A)
        var hotel2 = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m); // hotel 2 (se mueve a B)
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);

        var booking = BuildBookingService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            booking.UpdateHotelAsync(reserva.Id, hotel2.Id, BuildHotelUpdate(operatorB.PublicId.ToString(), 50_000m), CancellationToken.None));

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel2.Id);
        Assert.Equal(operatorA.Id, reloaded.SupplierId);
    }

    // ============================================================
    // (10) CAMBIO DE MONEDA (vía el método del service directamente, porque los requests de edición no exponen la
    //     moneda del servicio hoy): servicio pagado imputado + sin factura -> BLOQUEA con mensaje de MONEDA.
    // ============================================================
    [Fact]
    public async Task EnsureAnchor_CurrencyChange_ImputedPaid_Blocks_WithCurrencyMessage()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, _) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m, currency: "USD");
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m, currency: "USD");
        await ctx.SaveChangesAsync();

        var cancellationService = BuildCancellationService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cancellationService.EnsureServiceOperatorOrCurrencyChangeHasReceivableAnchorAsync(
                reserva.Id, CancellableServiceTable.Hotel, hotel.Id, isCurrencyChange: true, CancellationToken.None));
        Assert.Contains("moneda", ex.Message);
    }

    // ============================================================
    // (11) PRUEBA DE NECESIDAD (sentinela): SIN la guarda, el swap crudo del operador deja la caja de A en -50.000 y
    //     un reconcile MINTEA 50.000 de saldo a favor gastable. Es JUSTO la fuga que la guarda (test 1) impide.
    // ============================================================
    [Fact]
    public async Task RawOperatorSwap_WithoutGuard_LeavesCashNegative_AndReconcileMints_LeakDemonstration()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA, operatorB) = await SeedReservaWithTwoOperatorsAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentImputedToReserva(ctx, operatorA.Id, reserva.Id, amount: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id); // caja A = 0

        // Reproducimos el efecto que UpdateHotelAsync tendría SIN la guarda: swap crudo del operador + recalculo.
        var tracked = await ctx.HotelBookings.FirstAsync(h => h.Id == hotel.Id);
        tracked.SupplierId = operatorB.Id;
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id);
        await PersistSupplierBalanceAsync(ctx, operatorB.Id);

        var balanceA = await ctx.SupplierBalanceByCurrency.AsNoTracking()
            .FirstAsync(r => r.SupplierId == operatorA.Id && r.Currency == "ARS");
        Assert.Equal(-50_000m, balanceA.Balance);

        await SupplierCreditReconciler.ReconcileAsync(ctx, operatorA.Id, null, null, null, null, CancellationToken.None);
        Assert.Equal(50_000m, await PoolAsync(ctx, operatorA.Id));
    }
}
