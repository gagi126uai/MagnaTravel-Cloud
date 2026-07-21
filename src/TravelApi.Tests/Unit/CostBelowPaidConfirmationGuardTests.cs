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
/// Tanda P2 "circuito proveedor" (2026-07-21, decision D2 firmada por Gaston): AVISO (no bloqueo) cuando
/// se edita el costo (<c>NetCost</c>) de un servicio confirmado y el nuevo costo queda por debajo de lo
/// ya pagado al operador POR ESE SERVICIO puntual. A diferencia de la familia R1 (candado duro de
/// <see cref="OperatorReassignmentGuardTests"/>, que bloquea reasignar operador/moneda sin ancla), este
/// guard deja guardar si el caller confirma explícitamente (<c>ConfirmCostBelowPaid = true</c>).
///
/// <para>Alcance B de la misma tanda: verifica que <c>SupplierCreditReconciler</c> se dispare
/// automáticamente cuando la edición mueve plata (costo/proveedor/moneda), sin que haga falta un pago o
/// una anulación posterior para que el pool de saldo a favor del operador quede sincronizado.</para>
/// </summary>
public class CostBelowPaidConfirmationGuardTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"cost-below-paid-{Guid.NewGuid()}")
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

    /// <summary>
    /// SupplierService REAL (no mockeado): el guard nuevo necesita <c>GetCashPaidToOperatorForServiceAsync</c>
    /// con la query real, y <c>UpdateBalanceAsync</c> con el persister real (igual que en produccion).
    /// </summary>
    private static ISupplierService BuildRealSupplierService(AppDbContext ctx) =>
        new SupplierService(ctx, auditService: null, httpContextAccessor: null, logger: null, permissionResolver: null);

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
            BuildRealSupplierService(ctx),
            ctx,
            Mapper,
            NullLogger<BookingService>.Instance,
            SeeCostResolver(),
            AdminContext(),
            settingsService: null,
            auditService: null,
            cancellationService: BuildCancellationService(ctx));

    // ---------- seeds ----------

    private static async Task<(Reserva Reserva, Supplier OperatorA)> SeedReservaWithOperatorAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var operatorA = new Supplier { Name = "Operador A", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(operatorA);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-COSTGUARD-1", Name = "Reserva test", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return (reserva, operatorA);
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

    /// <summary>Pago al operador imputado a ESTE servicio puntual (recordKind + servicePublicId).</summary>
    private static void AddSupplierPaymentImputedToService(
        AppDbContext ctx, int supplierId, int reservaId, string serviceRecordKind, Guid servicePublicId,
        decimal amount, string currency = "ARS")
        => ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierId, ReservaId = reservaId, Amount = amount, Currency = currency, Method = "Transferencia",
            ServiceRecordKind = serviceRecordKind, ServicePublicId = servicePublicId,
        });

    private static async Task PersistSupplierBalanceAsync(AppDbContext ctx, int supplierId)
    {
        await SupplierDebtPersister.PersistAsync(ctx, supplierId);
        await ctx.SaveChangesAsync();
    }

    private static UpdateHotelRequest BuildHotelUpdate(
        string supplierPublicId, decimal netCost, bool confirmCostBelowPaid = false)
        => new(
            SupplierId: supplierPublicId, HotelName: "Hotel Test", StarRating: 4, City: "Bariloche", Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: netCost, SalePrice: netCost * 1.5m, Commission: netCost * 0.5m, Status: "Confirmado", Notes: null,
            RoomingAssignments: null, RateId: null, WorkflowStatus: "Confirmado",
            ConfirmCostBelowPaid: confirmCostBelowPaid);

    private static async Task<decimal> PoolAsync(AppDbContext ctx, int supplierId) =>
        (await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync())
            .Sum(e => e.RemainingBalance);

    // ============================================================
    // (1) DISPARA: costo nuevo < pagado, mismo proveedor y moneda, SIN confirmar -> 409 con Code, no muta.
    // ============================================================
    [Fact]
    public async Task LowerNetCost_BelowPaid_SameOperator_WithoutConfirm_ThrowsWithCode_AndDoesNotMutate()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA) = await SeedReservaWithOperatorAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentImputedToService(ctx, operatorA.Id, reserva.Id, ServicePaymentRecordKinds.Hotel, hotel.PublicId, amount: 50_000m);
        await ctx.SaveChangesAsync();

        var booking = BuildBookingService(ctx);

        var ex = await Assert.ThrowsAsync<CostBelowPaidConfirmationRequiredException>(() =>
            booking.UpdateHotelAsync(reserva.Id, hotel.Id, BuildHotelUpdate(operatorA.PublicId.ToString(), netCost: 30_000m), CancellationToken.None));

        Assert.Equal(CostBelowPaidConfirmationRequiredException.Codes.ConfirmationRequired, ex.Code);
        Assert.Contains("saldo a favor", ex.Message);
        // La diferencia (50.000 pagado - 30.000 costo nuevo = 20.000) aparece en criollo, formato es-AR.
        Assert.Contains("20.000,00", ex.Message);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(50_000m, reloaded.NetCost); // no muto: el guard corta ANTES de persistir
    }

    // ============================================================
    // (2) NO DISPARA: mismo caso de arriba, pero CON ConfirmCostBelowPaid=true -> guarda igual.
    // ============================================================
    [Fact]
    public async Task LowerNetCost_BelowPaid_WithConfirm_DoesNotThrow_AndPersists()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA) = await SeedReservaWithOperatorAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentImputedToService(ctx, operatorA.Id, reserva.Id, ServicePaymentRecordKinds.Hotel, hotel.PublicId, amount: 50_000m);
        await ctx.SaveChangesAsync();

        var booking = BuildBookingService(ctx);

        await booking.UpdateHotelAsync(
            reserva.Id, hotel.Id,
            BuildHotelUpdate(operatorA.PublicId.ToString(), netCost: 30_000m, confirmCostBelowPaid: true),
            CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(30_000m, reloaded.NetCost);
    }

    // ============================================================
    // (3) NO DISPARA: el costo baja pero sigue cubriendo lo pagado (40.000 >= 30.000 pagado).
    // ============================================================
    [Fact]
    public async Task LowerNetCost_StillCoversPaid_DoesNotThrow()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA) = await SeedReservaWithOperatorAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentImputedToService(ctx, operatorA.Id, reserva.Id, ServicePaymentRecordKinds.Hotel, hotel.PublicId, amount: 30_000m);
        await ctx.SaveChangesAsync();

        var booking = BuildBookingService(ctx);

        await booking.UpdateHotelAsync(
            reserva.Id, hotel.Id, BuildHotelUpdate(operatorA.PublicId.ToString(), netCost: 40_000m), CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(40_000m, reloaded.NetCost);
    }

    // ============================================================
    // (4) NO DISPARA: no hay ningun pago al operador imputado a este servicio.
    // ============================================================
    [Fact]
    public async Task LowerNetCost_NoPaymentsAtAll_DoesNotThrow()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA) = await SeedReservaWithOperatorAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        await ctx.SaveChangesAsync();

        var booking = BuildBookingService(ctx);

        await booking.UpdateHotelAsync(
            reserva.Id, hotel.Id, BuildHotelUpdate(operatorA.PublicId.ToString(), netCost: 10_000m), CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(10_000m, reloaded.NetCost);
    }

    // ============================================================
    // (5) ALCANCE B: bajar el costo por debajo de lo pagado (con confirmacion) sincroniza el pool de saldo a
    //     favor del operador EN EL MISMO REQUEST, sin necesitar un pago o anulacion posterior que dispare el
    //     reconciler. Antes de esta tanda, el pool quedaba en 0 hasta el proximo movimiento de plata.
    // ============================================================
    [Fact]
    public async Task LowerNetCost_BelowPaid_WithConfirm_ReconcilesSupplierCreditPoolInTheSameRequest()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA) = await SeedReservaWithOperatorAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        AddSupplierPaymentImputedToService(ctx, operatorA.Id, reserva.Id, ServicePaymentRecordKinds.Hotel, hotel.PublicId, amount: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id); // caja inicial: compra 50.000, pagado 50.000 -> pool 0

        Assert.Equal(0m, await PoolAsync(ctx, operatorA.Id));

        var booking = BuildBookingService(ctx);

        await booking.UpdateHotelAsync(
            reserva.Id, hotel.Id,
            BuildHotelUpdate(operatorA.PublicId.ToString(), netCost: 30_000m, confirmCostBelowPaid: true),
            CancellationToken.None);

        // Compra bajo a 30.000, lo pagado sigue en 50.000 -> 20.000 de saldo a favor, YA reflejado sin llamar
        // manualmente a SupplierCreditReconciler (es la aserción central del alcance B).
        Assert.Equal(20_000m, await PoolAsync(ctx, operatorA.Id));
    }

    // ============================================================
    // (6) No corre si cambia el operador: ese riesgo lo cubre el candado A4 (bloqueo duro), no este aviso.
    //     Reasignar a un operador SIN pagos imputados no bloquea ninguno de los dos guards.
    // ============================================================
    [Fact]
    public async Task ChangeOperator_UnpaidService_DoesNotTriggerCostBelowPaidGuard()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA) = await SeedReservaWithOperatorAsync(ctx);
        var operatorB = new Supplier { Name = "Operador B", IsActive = true };
        ctx.Suppliers.Add(operatorB);
        await ctx.SaveChangesAsync();
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 50_000m);
        await ctx.SaveChangesAsync();

        var booking = BuildBookingService(ctx);

        await booking.UpdateHotelAsync(
            reserva.Id, hotel.Id, BuildHotelUpdate(operatorB.PublicId.ToString(), netCost: 10_000m), CancellationToken.None);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal(operatorB.Id, reloaded.SupplierId);
        Assert.Equal(10_000m, reloaded.NetCost);
    }

    // ============================================================
    // (7) FIX B1 (review 2026-07-21): subir el costo saca el sobrepago que YA se aplico a otra reserva ->
    //     SupplierCreditReconciler rechaza con INV-SUPCREDIT-001. La excepcion tiene que PROPAGAR (no
    //     tragarse en un log ni convertirse en un 500 generico). InMemory no soporta transacciones, asi que
    //     acá solo se valida la propagacion; el rollback atomico real se valida en el test de integracion
    //     Postgres (CostBelowPaidReconcilerRollbackIntegrationTests).
    // ============================================================
    [Fact]
    public async Task RaiseNetCost_RemovesOverpaymentAlreadyAppliedElsewhere_ReconcilerExceptionPropagates()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA) = await SeedReservaWithOperatorAsync(ctx);
        var hotel = AddConfirmedHotel(ctx, reserva.Id, operatorA.Id, netCost: 30_000m);
        AddSupplierPaymentImputedToService(ctx, operatorA.Id, reserva.Id, ServicePaymentRecordKinds.Hotel, hotel.PublicId, amount: 50_000m);
        await ctx.SaveChangesAsync();
        await PersistSupplierBalanceAsync(ctx, operatorA.Id); // compra 30.000, pagado 50.000 -> sobrepago 20.000

        // Simula que ese sobrepago de 20.000 YA se aplico ENTERO a otra reserva (RemainingBalance en 0):
        // el reconciler no tiene de donde drenar cuando el sobrepago baje a 0.
        ctx.SupplierCreditEntries.Add(new SupplierCreditEntry
        {
            SupplierId = operatorA.Id, Currency = "ARS",
            CreditedAmount = 20_000m, RemainingBalance = 0m, IsFullyConsumed = true,
        });
        await ctx.SaveChangesAsync();

        var booking = BuildBookingService(ctx);

        // Sube el costo de 30.000 a 50.000: el nuevo costo cubre lo pagado (no dispara el AVISO de este
        // guard), pero elimina el sobrepago -> el reconciler intenta drenar 20.000 y no puede.
        var ex = await Assert.ThrowsAsync<TravelApi.Domain.Exceptions.BusinessInvariantViolationException>(() =>
            booking.UpdateHotelAsync(reserva.Id, hotel.Id, BuildHotelUpdate(operatorA.PublicId.ToString(), netCost: 50_000m), CancellationToken.None));

        Assert.Equal("INV-SUPCREDIT-001", ex.InvariantCode);
        Assert.Contains("saldo a favor", ex.Message);
    }

    // ============================================================
    // (8) NEGATIVO: crear un servicio (no editarlo) nunca dispara el guard de costo<pagado — el servicio
    //     todavia no existe, no puede tener pagos imputados. Sirve de red de regresion: si algun dia alguien
    //     cablea el guard tambien en el alta por error, este test lo cachea.
    // ============================================================
    [Fact]
    public async Task CreateHotel_NeverTriggersCostBelowPaidGuard()
    {
        await using var ctx = NewContext();
        var (reserva, operatorA) = await SeedReservaWithOperatorAsync(ctx);
        // ADR-031: un hotel que nace "Confirmado" (resuelto) exige titular. No es lo que este test valida
        // (eso lo cubre Adr031PassengerNominalGateTests); solo hace falta un pasajero para que el alta no
        // explote por una regla ajena al guard de costo que este test ejercita.
        ctx.Passengers.Add(new Passenger { ReservaId = reserva.Id, FullName = "Titular" });
        await ctx.SaveChangesAsync();

        var booking = BuildBookingService(ctx);

        var request = new CreateHotelRequest(
            SupplierId: operatorA.PublicId.ToString(), HotelName: "Hotel Nuevo", StarRating: 3,
            City: "Bariloche", Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1,
            ConfirmationNumber: null, NetCost: 1_000m, SalePrice: 2_000m, Commission: 1_000m, Notes: null,
            WorkflowStatus: "Confirmado");

        var dto = await booking.CreateHotelAsync(reserva.Id, request, CancellationToken.None);

        Assert.Equal(1_000m, dto.NetCost);
    }
}
