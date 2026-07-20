using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// R1 — VARIANTE TOTAL (plata viva, 2026-06-30): la guarda gemela del candado que ya protege la cancelacion de UN
/// servicio, ahora aplicada a "Anular con saldo a favor" (<see cref="ReservaService.AnnulWithPaymentsToCreditAsync"/>).
///
/// <para><b>La fuga que cierra</b>: ese flujo cancela TODOS los servicios vivos (caja del operador queda negativa por
/// lo pagado) pero NO crea ninguna <c>BookingCancellationLine</c>. Como el receivable "me tiene que devolver" (Y) se
/// deriva EXCLUSIVAMENTE de esas lineas, sin linea Y=0 y el <see cref="SupplierCreditReconciler"/> materializaria el
/// negativo de caja como saldo a favor GASTABLE — plata que el operador en realidad debe devolver. Los tests pasan por
/// el PATH REAL (no construyen estados a mano) y demuestran: (1) BLOQUEA cuando hay plata al operador sin factura;
/// (2) NO bloquea servicios impagos; (3) NO bloquea ni rompe una reserva sin operador; (4) con factura viva sigue
/// derivando al camino formal (precondicion 4, intacta); (5) tras el fix un reconcile NO mintea; (6) sin la guarda
/// cableada el reconcile SI mintea (prueba de que la guarda es load-bearing).</para>
///
/// <para><b>Nota InMemory</b>: usa el flujo real del service. InMemory no soporta transacciones (la rama IsRelational
/// corre el mismo cuerpo sin transaccion); la atomicidad REAL se valida en integracion Postgres. La cuenta (estado,
/// caja, pool) si se verifica aca.</para>
/// </summary>
public class AnnulWithCreditOperatorReceivableAnchorTests
{
    private const string ValidReason = "Cliente desistio del viaje por fuerza mayor";

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"annul-anchor-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static readonly IMapper Mapper =
        new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

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

    /// <summary>Service de cancelaciones REAL compartiendo el MISMO contexto (como en DI scoped).</summary>
    private static IBookingCancellationService BuildCancellationService(AppDbContext ctx) =>
        new BookingCancellationService(
            ctx, new Mock<IInvoiceService>().Object, new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object, NullLogger<BookingCancellationService>.Instance,
            SettingsService(), new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

    /// <summary>ReservaService con la guarda CABLEADA (Admin: bypassa authz; nos enfocamos en la guarda de plata).</summary>
    private static ReservaService BuildReservaServiceWithGuard(AppDbContext ctx) =>
        new(ctx, Mapper, SettingsService(), BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: AdminContext(),
            autoStateService: null, auditService: null,
            cancellationService: BuildCancellationService(ctx));

    /// <summary>ReservaService SIN la guarda (replica el comportamiento PRE-FIX: cancellationService null).</summary>
    private static ReservaService BuildReservaServiceWithoutGuard(AppDbContext ctx) =>
        new(ctx, Mapper, SettingsService(), BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: AdminContext(),
            autoStateService: null, auditService: null,
            cancellationService: null);

    /// <summary>
    /// Siembra una reserva EN FIRME, SIN factura, con un hotel del operador y (opcionalmente) plata pagada al
    /// operador por ese servicio y un cobro vivo del cliente. Devuelve (reservaPublicId, supplierId, hotelId).
    /// </summary>
    private static async Task<(string ReservaPublicId, int SupplierId, int HotelId)> SeedFirmReservaAsync(
        AppDbContext ctx, bool paidToOperator, bool clientPayment)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador Test", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-ANCHOR-1", Name = "Reserva test", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id,
            Status = "Confirmado", NetCost = 50_000m, SalePrice = 75_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);

        if (paidToOperator)
        {
            // Pago al operador por el total del costo: tras cancelar el servicio la caja quedaria en -50.000.
            ctx.SupplierPayments.Add(new SupplierPayment
            {
                SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 50_000m,
                Currency = "ARS", Method = "Transferencia",
            });
        }

        if (clientPayment)
        {
            ctx.Payments.Add(NewClientPayment(reserva.Id, 75_000m));
        }

        await ctx.SaveChangesAsync();

        return (reserva.PublicId.ToString(), supplier.Id, hotel.Id);
    }

    /// <summary>
    /// Siembra una reserva EN FIRME, SIN factura, con UN servicio generico SIN operador (SupplierId null) y un cobro
    /// vivo del cliente. Sirve para el caso "sin servicios con operador": no hay receivable posible y la guarda no
    /// debe bloquear ni romper. Devuelve (reservaPublicId, genericServiceId).
    /// </summary>
    private static async Task<(string ReservaPublicId, int ServiceId)> SeedFirmReservaWithoutOperatorAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-ANCHOR-NOOP", Name = "Reserva sin operador", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = new ServicioReserva
        {
            ReservaId = reserva.Id, ServiceType = "Otro", ProductType = "Otro", Description = "Servicio sin operador",
            ConfirmationNumber = "NOOP", Status = "Confirmado", Currency = "ARS",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 75_000m, NetCost = 0m, Commission = 75_000m,
            SupplierId = null, ConfirmedAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow,
        };
        ctx.Servicios.Add(service);
        ctx.Payments.Add(NewClientPayment(reserva.Id, 75_000m));
        await ctx.SaveChangesAsync();

        return (reserva.PublicId.ToString(), service.Id);
    }

    private static Payment NewClientPayment(int reservaId, decimal amount) => new()
    {
        ReservaId = reservaId, Amount = amount, Currency = "ARS", Method = "Transfer",
        Status = "Paid", EntryType = PaymentEntryTypes.Payment, AffectsCash = true, PaidAt = DateTime.UtcNow,
    };

    private static async Task<decimal> PoolAsync(AppDbContext ctx, int supplierId) =>
        (await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync())
            .Sum(e => e.RemainingBalance);

    // ============================================================
    // (1) FUGA: hay plata pagada al operador y NO hay factura -> BLOQUEA y no muta nada.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_PaidToOperator_NoInvoice_Blocks_AndDoesNotMutate()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, supplierId, hotelId) =
            await SeedFirmReservaAsync(ctx, paidToOperator: true, clientPayment: true);

        var service = BuildReservaServiceWithGuard(ctx);

        // Plata pagada al operador + sin factura que ancle el receivable -> la guarda lanza (409 en el controller).
        // Tanda 3 "contrato pantalla-motor" (2026-07-20): AnnulWithCreditRejectedException agrega el
        // Code=UnanchoredOperatorRefund al body 409, sin cambiar el mensaje (subclase de InvalidOperationException).
        var ex = await Assert.ThrowsAsync<AnnulWithCreditRejectedException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin"));
        Assert.Equal(AnnulWithCreditRejectedException.Codes.UnanchoredOperatorRefund, ex.Code);

        // NADA mutado: la reserva sigue Confirmed y el servicio sigue VIVO (no se cancelo).
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.PublicId.ToString() == reservaPublicId);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
        var hotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.Equal("Confirmado", hotel.Status);

        // No se genero saldo a favor del cliente NI saldo a favor (gastable) del operador.
        Assert.Empty(await ctx.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync());
    }

    // ============================================================
    // (1-bis) Tras el bloqueo, un reconcile posterior NO mintea (la fuga quedo cerrada).
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_BlockedCase_LaterReconcile_DoesNotMintCredit()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, supplierId, _) =
            await SeedFirmReservaAsync(ctx, paidToOperator: true, clientPayment: true);

        var service = BuildReservaServiceWithGuard(ctx);

        await Assert.ThrowsAsync<AnnulWithCreditRejectedException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin"));

        // El servicio sigue vivo (la anulacion se bloqueo): la caja del operador NO es negativa, asi que cualquier
        // reconcile posterior (disparado por otro pago/edicion) no encuentra sobrepago que materializar.
        await SupplierCreditReconciler.ReconcileAsync(
            ctx, supplierId, sourceSupplierPaymentId: null, actorUserId: null, actorUserName: null,
            auditService: null, CancellationToken.None);

        Assert.Equal(0m, await PoolAsync(ctx, supplierId));
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync());
    }

    // ============================================================
    // (2) Servicio IMPAGO al operador (RefundCap 0) -> NO bloquea: anula normal.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_UnpaidOperator_NoInvoice_Annuls_WithoutBlocking()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, supplierId, hotelId) =
            await SeedFirmReservaAsync(ctx, paidToOperator: false, clientPayment: true);

        var service = BuildReservaServiceWithGuard(ctx);

        var dto = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin");

        // Sin plata al operador no hay receivable -> la guarda no bloquea: anula y el servicio queda cancelado.
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        var hotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.Equal(WorkflowStatuses.Cancelado, hotel.Status);

        // El cobro del cliente se convirtio en saldo a favor; NO se minteo saldo a favor del operador.
        Assert.NotEmpty(await ctx.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync());
    }

    // ============================================================
    // (3) Reserva SIN servicios con operador -> NO bloquea ni rompe (no lanza "sin supplier").
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_NoOperatorService_Annuls_WithoutThrowingNoSupplier()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, serviceId) = await SeedFirmReservaWithoutOperatorAsync(ctx);

        var service = BuildReservaServiceWithGuard(ctx);

        // El servicio no tiene operador: BuildCancellationLinesAsync lanzaria "no tiene servicios con Supplier"; la
        // guarda lo trata como "no hay receivable" y NO bloquea ni propaga ese throw.
        var dto = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin");

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        var generic = await ctx.Servicios.AsNoTracking().FirstAsync(s => s.Id == serviceId);
        Assert.Equal(WorkflowStatuses.Cancelado, generic.Status);
    }

    // ============================================================
    // (4) Con factura viva: la precondicion 4 deriva al camino formal (la guarda no cambia eso).
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_WithLiveInvoice_StillDerivesToFormalPath_Unchanged()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, supplierId, _) =
            await SeedFirmReservaAsync(ctx, paidToOperator: true, clientPayment: true);

        // Factura A con CAE vivo (no NC, no anulada).
        var reservaId = await ctx.Reservas.AsNoTracking()
            .Where(r => r.PublicId.ToString() == reservaPublicId).Select(r => r.Id).FirstAsync();
        ctx.Invoices.Add(new Invoice
        {
            ReservaId = reservaId, TipoComprobante = 1, CAE = "12345678901234",
            AnnulmentStatus = AnnulmentStatus.None,
        });
        await ctx.SaveChangesAsync();

        var service = BuildReservaServiceWithGuard(ctx);

        // Sigue rechazando por la precondicion fiscal (factura viva) -> deriva al camino formal de NC, sin tocar nada.
        // Tanda 3 (2026-07-20): Code=LiveInvoice, mismo mensaje de siempre.
        var ex = await Assert.ThrowsAsync<AnnulWithCreditRejectedException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin"));
        Assert.Equal(AnnulWithCreditRejectedException.Codes.LiveInvoice, ex.Code);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.PublicId.ToString() == reservaPublicId);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
        Assert.Empty(await ctx.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync());
    }

    // ============================================================
    // (5) PRUEBA DE NECESIDAD: sin la guarda cableada, el flujo cancela los servicios y un reconcile MINTEA la fuga.
    //     Documenta por que la guarda es load-bearing (NO es el comportamiento deseado; es la fuga que cerramos).
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_WithoutGuard_CancelsServices_AndReconcileMintsCredit_LeakDemonstration()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, supplierId, hotelId) =
            await SeedFirmReservaAsync(ctx, paidToOperator: true, clientPayment: true);

        // Service SIN la guarda (replica el comportamiento pre-fix).
        var service = BuildReservaServiceWithoutGuard(ctx);

        var dto = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin");

        // Sin guarda, la anulacion procede: el servicio queda cancelado y la caja del operador cae a -50.000.
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        var hotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.Equal(WorkflowStatuses.Cancelado, hotel.Status);
        var balanceRow = await ctx.SupplierBalanceByCurrency.AsNoTracking()
            .FirstAsync(r => r.SupplierId == supplierId && r.Currency == "ARS");
        Assert.Equal(-50_000m, balanceRow.Balance);

        // LA FUGA: sin la linea que ancle el receivable Y, el reconcile materializa el negativo como saldo a favor
        // GASTABLE del operador. Esto es JUSTO lo que la guarda (test 1) impide.
        await SupplierCreditReconciler.ReconcileAsync(
            ctx, supplierId, sourceSupplierPaymentId: null, actorUserId: null, actorUserName: null,
            auditService: null, CancellationToken.None);

        Assert.Equal(50_000m, await PoolAsync(ctx, supplierId));
    }

    // ============================================================
    // Refuerzo de cobertura (post-review, 2026-06-30). Seeds locales para escenarios multi-servicio.
    // ============================================================

    private static async Task<(Customer Customer, Reserva Reserva)> SeedFirmReservaShellAsync(AppDbContext ctx, string numero)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = numero, Name = "Reserva test", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return (customer, reserva);
    }

    private static HotelBooking AddHotel(AppDbContext ctx, int reservaId, int supplierId, decimal netCost, string currency)
    {
        var hotel = new HotelBooking
        {
            ReservaId = reservaId, SupplierId = supplierId, Status = "Confirmado",
            NetCost = netCost, SalePrice = netCost * 1.5m, Currency = currency,
        };
        ctx.HotelBookings.Add(hotel);
        return hotel;
    }

    private static void AddSupplierPayment(AppDbContext ctx, int supplierId, int reservaId, decimal amount, string currency)
        => ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierId, ReservaId = reservaId, Amount = amount, Currency = currency, Method = "Transferencia",
        });

    // ============================================================
    // (6) MULTIMONEDA: servicio ARS impago + servicio USD PAGADO -> la suma de caps cross-currency (>0) bloquea.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_CrossCurrency_PaidUsdService_Blocks()
    {
        await using var ctx = NewContext();
        var supplier = new Supplier { Name = "Operador USD", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        var (_, reserva) = await SeedFirmReservaShellAsync(ctx, "F-XCCY");

        // ARS impago (cap 0) + USD pagado (cap 50.000 USD). Sin factura.
        AddHotel(ctx, reserva.Id, supplier.Id, netCost: 40_000m, currency: "ARS"); // impago
        AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "USD"); // pagado abajo
        AddSupplierPayment(ctx, supplier.Id, reserva.Id, amount: 50_000m, currency: "USD");
        ctx.Payments.Add(NewClientPayment(reserva.Id, 75_000m));
        await ctx.SaveChangesAsync();

        var service = BuildReservaServiceWithGuard(ctx);

        // El cap en USD (50.000) hace que la suma cross-currency sea > 0 -> bloquea igual que en ARS.
        await Assert.ThrowsAsync<AnnulWithCreditRejectedException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin"));

        var reloaded = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, reloaded.Status);
        Assert.Empty(await ctx.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplier.Id).ToListAsync());
    }

    // ============================================================
    // (7) MULTI-OPERADOR: uno PAGADO + uno IMPAGO -> bloquea por el pagado.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_MultiOperator_OnePaidOneUnpaid_Blocks()
    {
        await using var ctx = NewContext();
        var paidSupplier = new Supplier { Name = "Operador Pagado", IsActive = true };
        var unpaidSupplier = new Supplier { Name = "Operador Impago", IsActive = true };
        ctx.Suppliers.AddRange(paidSupplier, unpaidSupplier);
        await ctx.SaveChangesAsync();
        var (_, reserva) = await SeedFirmReservaShellAsync(ctx, "F-MULTIOP");

        AddHotel(ctx, reserva.Id, paidSupplier.Id, netCost: 50_000m, currency: "ARS");
        AddHotel(ctx, reserva.Id, unpaidSupplier.Id, netCost: 30_000m, currency: "ARS");
        AddSupplierPayment(ctx, paidSupplier.Id, reserva.Id, amount: 50_000m, currency: "ARS"); // solo al primero
        ctx.Payments.Add(NewClientPayment(reserva.Id, 100_000m));
        await ctx.SaveChangesAsync();

        var service = BuildReservaServiceWithGuard(ctx);

        await Assert.ThrowsAsync<AnnulWithCreditRejectedException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin"));

        var reloaded = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, reloaded.Status);
    }

    // ============================================================
    // (7-bis) MULTI-OPERADOR: ambos IMPAGOS -> NO bloquea, anula normal.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_MultiOperator_BothUnpaid_Annuls()
    {
        await using var ctx = NewContext();
        var s1 = new Supplier { Name = "Operador 1", IsActive = true };
        var s2 = new Supplier { Name = "Operador 2", IsActive = true };
        ctx.Suppliers.AddRange(s1, s2);
        await ctx.SaveChangesAsync();
        var (_, reserva) = await SeedFirmReservaShellAsync(ctx, "F-MULTIOP2");

        AddHotel(ctx, reserva.Id, s1.Id, netCost: 50_000m, currency: "ARS");
        AddHotel(ctx, reserva.Id, s2.Id, netCost: 30_000m, currency: "ARS");
        // Sin pagos a ningun operador -> cap 0 en ambos.
        ctx.Payments.Add(NewClientPayment(reserva.Id, 100_000m));
        await ctx.SaveChangesAsync();

        var service = BuildReservaServiceWithGuard(ctx);

        var dto = await service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().ToListAsync());
    }

    // ============================================================
    // (8a) PARCIAL-PREVIA + residual sin anclar: se pago al operador por 2 servicios, uno ya tiene su linea
    //      (parcial previa), el OTRO sigue pagado sin linea -> el residual dispara la guarda.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_PreviousPartialLine_WithResidualUnanchored_Blocks()
    {
        await using var ctx = NewContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        var (customer, reserva) = await SeedFirmReservaShellAsync(ctx, "F-PARTIAL-A");

        // Hotel A ya cancelado por una parcial previa (su linea ancla 50.000); Hotel B sigue vivo y pagado.
        var hotelA = AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "ARS");
        hotelA.Status = WorkflowStatuses.Cancelado;
        var hotelB = AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "ARS");
        // Se pago al operador por AMBOS (100.000): la parcial previa solo anclo 50.000 -> quedan 50.000 sin anclar.
        AddSupplierPayment(ctx, supplier.Id, reserva.Id, amount: 100_000m, currency: "ARS");
        ctx.Payments.Add(NewClientPayment(reserva.Id, 150_000m));
        await ctx.SaveChangesAsync();

        await SeedNonAbortedPartialLineAsync(ctx, reserva, customer.Id, supplier.Id, hotelA.Id, refundCap: 50_000m);

        var service = BuildReservaServiceWithGuard(ctx);

        await Assert.ThrowsAsync<AnnulWithCreditRejectedException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin"));

        var reloaded = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, reloaded.Status);
    }

    // ============================================================
    // (8b) PARCIAL-PREVIA + todo anclado (no over-block): la parcial previa ya reservo TODO lo pagado al
    //      operador -> la deduccion existingLineConsumption deja el pool en 0 -> cap 0 -> NO bloquea. Y ademas
    //      un reconcile posterior NO mintea (el receivable ya esta anclado por la linea previa).
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_PreviousPartialLine_FullyAnchored_DoesNotOverBlock_AndNoMint()
    {
        await using var ctx = NewContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        var (customer, reserva) = await SeedFirmReservaShellAsync(ctx, "F-PARTIAL-B");

        var hotelA = AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "ARS");
        hotelA.Status = WorkflowStatuses.Cancelado;
        var hotelB = AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "ARS");
        // Se pago al operador SOLO 50.000 (lo de Hotel A), que la parcial previa ya anclo por completo. Hotel B
        // esta impago -> no hay residual.
        AddSupplierPayment(ctx, supplier.Id, reserva.Id, amount: 50_000m, currency: "ARS");
        ctx.Payments.Add(NewClientPayment(reserva.Id, 150_000m));
        await ctx.SaveChangesAsync();

        await SeedNonAbortedPartialLineAsync(ctx, reserva, customer.Id, supplier.Id, hotelA.Id, refundCap: 50_000m);

        var service = BuildReservaServiceWithGuard(ctx);

        // No over-block: todo lo pagado ya esta anclado por la linea previa -> cap residual 0 -> anula.
        var dto = await service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        // Y no hay fuga: el reconcile no mintea (la linea previa ancla el receivable Y=50.000 contra la caja -50.000).
        await SupplierCreditReconciler.ReconcileAsync(
            ctx, supplier.Id, sourceSupplierPaymentId: null, actorUserId: null, actorUserName: null,
            auditService: null, CancellationToken.None);
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }

    /// <summary>
    /// Siembra DIRECTAMENTE (estilo de este archivo) una cancelacion parcial YA anclada: un BookingCancellation NO
    /// abortado + una BookingCancellationLine Partial para el servicio dado, con su RefundCap. Representa el estado
    /// que dejaria una cancelacion parcial previa; lo lee la deduccion <c>existingLineConsumption</c> de
    /// <c>AssignRefundCapsAsync</c>. No agrega factura viva: el guard debe seguir corriendo el calculo de caps.
    /// </summary>
    private static async Task SeedNonAbortedPartialLineAsync(
        AppDbContext ctx, Reserva reserva, int customerId, int supplierId, int hotelId, decimal refundCap)
    {
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customerId, SupplierId = supplierId,
            OriginatingInvoiceId = 999_999, // dangling en InMemory (sin FK); no hay factura viva a proposito.
            Status = BookingCancellationStatus.Drafted, Reason = "Cancelacion parcial previa de un servicio",
            DraftedByUserId = "vendedor-1",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierId, ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = hotelId, Scope = BookingCancellationLineScope.Partial, Currency = "ARS",
            LineSaleAmount = refundCap, RefundCap = refundCap, ReceivedRefundAmount = 0m,
        });
        await ctx.SaveChangesAsync();
    }

    // ============================================================
    // (9) SOBREPAGO al operador (pool > costo): igual bloquea (cap topeado por costo sigue > 0).
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_OverpaidOperator_PoolAboveCost_StillBlocks()
    {
        await using var ctx = NewContext();
        var supplier = new Supplier { Name = "Operador Sobrepagado", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        var (_, reserva) = await SeedFirmReservaShellAsync(ctx, "F-OVERPAY");

        AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "ARS");
        // Se le pago 80.000 al operador por un servicio que costo 50.000: el cap se topea en 50.000 (> 0) -> bloquea.
        AddSupplierPayment(ctx, supplier.Id, reserva.Id, amount: 80_000m, currency: "ARS");
        ctx.Payments.Add(NewClientPayment(reserva.Id, 75_000m));
        await ctx.SaveChangesAsync();

        var service = BuildReservaServiceWithGuard(ctx);

        await Assert.ThrowsAsync<AnnulWithCreditRejectedException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin"));

        var reloaded = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, reloaded.Status);
    }

    // ============================================================
    // (10) MAPEO CONTROLLER: el endpoint AnnulWithCredit devuelve 409 con el mensaje de la guarda cuando el
    //      service lanza InvalidOperationException. Precedente: ReservasControllerServiceRouteTests construye el
    //      controller con mocks. Aqui mockeamos IReservaService para asertar SOLO el mapeo (no re-testeamos la logica).
    // ============================================================
    [Fact]
    public async Task AnnulWithCreditController_MapsGuardException_To409Conflict_WithMessage()
    {
        const string guardMessage =
            "No se puede anular esta reserva con saldo a favor todavía: ya se le pagó al operador por uno o más " +
            "servicios y la reserva aún no tiene factura emitida para registrar el reembolso a tu favor. Emití la " +
            "factura de venta o gestioná el reembolso con el operador antes de anular la reserva.";

        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(s => s.AnnulWithPaymentsToCreditAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(guardMessage));

        var controller = new ReservasController(
            reservaService.Object, Mock.Of<IVoucherService>(), Mock.Of<ITimelineService>(),
            Mock.Of<ISupplierService>(), Mock.Of<IEntityReferenceResolver>(), Mock.Of<IBookingService>(),
            NullLogger<ReservasController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "admin-1") }, "Test")),
            },
        };

        var result = await controller.AnnulWithCredit(
            "reserva-1", new AnnulWithCreditRequest(ValidReason), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        // El body ({ message = ... }) expone el mensaje amable de la guarda (sin jerga ni internals). Leemos la
        // propiedad directamente (no serializamos: System.Text.Json escaparia los acentos y rompe la comparacion).
        var messageProperty = conflict.Value!.GetType().GetProperty("message");
        Assert.NotNull(messageProperty);
        Assert.Equal(guardMessage, (string?)messageProperty!.GetValue(conflict.Value));

        // El body de un InvalidOperationException "a secas" (sin Code) NO trae la propiedad `code` — es el
        // fallback de siempre, intacto (Tanda 3 solo agrega `code` cuando el tipo real es AnnulWithCreditRejectedException).
        Assert.Null(conflict.Value.GetType().GetProperty("code"));
    }

    // ============================================================
    // (11) MAPEO CONTROLLER — Tanda 3 "contrato pantalla-motor" (2026-07-20): cuando el service lanza
    //      AnnulWithCreditRejectedException, el controller SUMA `code` al body 409 sin tocar el `message`
    //      de siempre (envelope aditivo, Decision C del plan). Mismo precedente de mock que (10).
    // ============================================================
    [Fact]
    public async Task AnnulWithCreditController_MapsRejectedException_To409Conflict_WithCodeAndMessage()
    {
        const string guardMessage =
            "No se puede anular esta reserva con saldo a favor todavía: ya se le pagó al operador por uno o más " +
            "servicios y la reserva aún no tiene factura emitida para registrar el reembolso a tu favor. Emití la " +
            "factura de venta o gestioná el reembolso con el operador antes de anular la reserva.";

        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(s => s.AnnulWithPaymentsToCreditAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AnnulWithCreditRejectedException(
                AnnulWithCreditRejectedException.Codes.UnanchoredOperatorRefund, guardMessage));

        var controller = new ReservasController(
            reservaService.Object, Mock.Of<IVoucherService>(), Mock.Of<ITimelineService>(),
            Mock.Of<ISupplierService>(), Mock.Of<IEntityReferenceResolver>(), Mock.Of<IBookingService>(),
            NullLogger<ReservasController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "admin-1") }, "Test")),
            },
        };

        var result = await controller.AnnulWithCredit(
            "reserva-1", new AnnulWithCreditRequest(ValidReason), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        // El message sigue siendo EXACTAMENTE el mismo que veia el usuario antes de esta tanda (leemos la
        // propiedad directamente: serializar escaparia los acentos y rompe la comparacion).
        var messageProperty = conflict.Value!.GetType().GetProperty("message");
        Assert.NotNull(messageProperty);
        Assert.Equal(guardMessage, (string?)messageProperty!.GetValue(conflict.Value));

        // Y ahora, ADEMAS, viaja el code estable que el frontend (Tanda 4) usa para el cartel con camino.
        var codeProperty = conflict.Value.GetType().GetProperty("code");
        Assert.NotNull(codeProperty);
        Assert.Equal(
            AnnulWithCreditRejectedException.Codes.UnanchoredOperatorRefund,
            (string?)codeProperty!.GetValue(conflict.Value));
    }
}
