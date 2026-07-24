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
/// Obra "anular sin factura" (2026-07-23, decisión del dueño; respaldo fiscal Ley de IVA art. 5 inc. b):
/// REESCRITURA de este archivo al invariante nuevo. Antes (R1, 2026-06-30) "Anular con saldo a favor"
/// BLOQUEABA cuando había plata pagada al operador sin factura de venta. Ese bloqueo se ELIMINÓ: ahora la
/// operación SIEMPRE procede, y en su lugar se deja SIEMPRE una <see cref="BookingCancellationLine"/> que
/// ancla el receivable "el operador me tiene que devolver" — con factura como ancla fiscal si existe, o SIN
/// ancla (<c>BookingCancellation.OriginatingInvoiceId</c> null) si no existe.
///
/// <para><b>La fuga que sigue cerrada, con otro mecanismo</b>: ese flujo cancela TODOS los servicios vivos (la
/// caja del operador queda negativa por lo pagado). Antes, sin ninguna <c>BookingCancellationLine</c> para
/// representar el receivable, el <see cref="SupplierCreditReconciler"/> materializaría ese negativo como
/// saldo a favor GASTABLE (plata que el operador todavía debe devolver, no crédito del cliente). Ahora la
/// línea que <see cref="BookingCancellationService.EnsureOperatorReceivableAnchorLinesAsync"/> deja ancla
/// exactamente esa plata, así que el reconciler nunca la mintea. El test núcleo de este archivo
/// (<c>AnnulWithCredit_PaidToOperator_NoInvoice_CreatesAnchorLine_AndDoesNotMintCredit</c>) es la red que
/// prueba esto de punta a punta: crea la línea, corre el reconciler DE VERDAD, y aserta que el pool queda en
/// 0. Sin esa aserción el diseño entero pierde su red (B3 del brief de esta obra).</para>
///
/// <para><b>Nota InMemory</b>: usa el flujo real del service. InMemory no soporta transacciones (la rama
/// IsRelational corre el mismo cuerpo sin transacción); la atomicidad REAL se valida en integración Postgres.
/// La cuenta (estado, caja, pool, línea) sí se verifica acá.</para>
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

    /// <summary>ReservaService con el ancla CABLEADA (Admin: bypassa authz; nos enfocamos en la plata).</summary>
    private static ReservaService BuildReservaServiceWithAnchor(AppDbContext ctx) =>
        new(ctx, Mapper, SettingsService(), BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: AdminContext(),
            autoStateService: null, auditService: null,
            cancellationService: BuildCancellationService(ctx));

    /// <summary>
    /// ReservaService SIN el ancla (cancellationService null, mismo patrón que un test/entorno con DI mal
    /// cableado). Documenta que, sin ella, la fuga original reaparece — sigue siendo un test de necesidad.
    /// </summary>
    private static ReservaService BuildReservaServiceWithoutAnchor(AppDbContext ctx) =>
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
    /// vivo del cliente. Sirve para el caso "sin servicios con operador": no hay receivable posible y no debe crear
    /// ninguna linea ni romper. Devuelve (reservaPublicId, genericServiceId).
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

    /// <summary>Corre el reconciler de saldo a favor del operador (mismo helper que se dispara en producción).</summary>
    private static Task ReconcileAsync(AppDbContext ctx, int supplierId) =>
        SupplierCreditReconciler.ReconcileAsync(
            ctx, supplierId, sourceSupplierPaymentId: null, actorUserId: null, actorUserName: null,
            auditService: null, CancellationToken.None);

    // ============================================================
    // (1) NÚCLEO B3: plata pagada al operador, SIN factura -> anula igual, deja la línea-ancla con el RefundCap
    //     correcto, Y = Σ RefundCap, y un reconcile posterior NO mintea esa plata como saldo a favor.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_PaidToOperator_NoInvoice_CreatesAnchorLine_AndDoesNotMintCredit()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, supplierId, hotelId) =
            await SeedFirmReservaAsync(ctx, paidToOperator: true, clientPayment: true);

        var service = BuildReservaServiceWithAnchor(ctx);

        // Ya NO lanza: la anulación procede aunque no haya factura de venta (decisión del dueño 2026-07-23).
        var dto = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        var reservaId = await ctx.Reservas.AsNoTracking()
            .Where(r => r.PublicId.ToString() == reservaPublicId).Select(r => r.Id).FirstAsync();
        var hotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.Equal(WorkflowStatuses.Cancelado, hotel.Status);

        // (a) La línea existe, con el RefundCap correcto (50.000, tope = lo pagado = lo que costó) y SIN ancla
        //     fiscal (el BC padre queda con OriginatingInvoiceId null: no hay factura de venta).
        var bc = await ctx.BookingCancellations.AsNoTracking()
            .Include(b => b.Lines)
            .SingleAsync(b => b.ReservaId == reservaId);
        Assert.Null(bc.OriginatingInvoiceId);
        // Ultima pieza de la obra (2026-07-23): un BC sin ancla con plata real que reclamar (RefundCap > 0) NO
        // se queda en Drafted para siempre (guard R4 nunca lo alcanza — no hay factura que anular). Salta
        // directo a AwaitingOperatorRefund para que el reembolso real del operador se pueda registrar.
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);
        var line = Assert.Single(bc.Lines);
        Assert.Equal(CancellableServiceTable.Hotel, line.ServiceTable);
        Assert.Equal(hotelId, line.ServiceId);
        Assert.Equal(50_000m, line.RefundCap);

        // (b) Y (el receivable vivo que ve el extracto del operador) = Σ RefundCap de sus líneas = 50.000.
        var circuit = await SupplierCancellationCircuitReader.LoadAsync(ctx, supplierId, CancellationToken.None);
        Assert.Equal(50_000m, circuit.ReceivableByCurrency.GetValueOrDefault("ARS"));

        // (c) LA RED: correr el reconciler DE VERDAD no mintea esa plata como saldo a favor consumible. Sin la
        //     línea-ancla, el pool daría 50.000 (ver el test "sin el ancla cableada" más abajo).
        await ReconcileAsync(ctx, supplierId);
        Assert.Equal(0m, await PoolAsync(ctx, supplierId));
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync());

        // El cobro del cliente SÍ se convierte en saldo a favor del cliente (eso es independiente del operador).
        Assert.NotEmpty(await ctx.ClientCreditEntries.AsNoTracking().ToListAsync());
    }

    // ============================================================
    // (2) Servicio IMPAGO al operador (RefundCap 0) -> no crea ninguna línea (nada que anclar).
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_UnpaidOperator_NoInvoice_Annuls_WithoutAnchorLine()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, supplierId, hotelId) =
            await SeedFirmReservaAsync(ctx, paidToOperator: false, clientPayment: true);

        var service = BuildReservaServiceWithAnchor(ctx);

        var dto = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin");

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        var hotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.Equal(WorkflowStatuses.Cancelado, hotel.Status);

        // Sin plata al operador no hay receivable -> ningún BC/línea se crea (no tiene sentido anclar 0).
        Assert.Empty(await ctx.BookingCancellations.AsNoTracking().ToListAsync());

        Assert.NotEmpty(await ctx.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync());
    }

    // ============================================================
    // (3) Reserva SIN servicios con operador -> no rompe (no lanza "sin supplier") ni crea líneas.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_NoOperatorService_Annuls_WithoutThrowingNoSupplier()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, serviceId) = await SeedFirmReservaWithoutOperatorAsync(ctx);

        var service = BuildReservaServiceWithAnchor(ctx);

        var dto = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin");

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        var generic = await ctx.Servicios.AsNoTracking().FirstAsync(s => s.Id == serviceId);
        Assert.Equal(WorkflowStatuses.Cancelado, generic.Status);
        Assert.Empty(await ctx.BookingCancellations.AsNoTracking().ToListAsync());
    }

    // ============================================================
    // (4) Con factura viva: la precondición fiscal (paso 4) sigue derivando al camino formal, SIN cambios —
    //     este flujo nunca se llega a evaluar la plata del operador.
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

        var service = BuildReservaServiceWithAnchor(ctx);

        // Sigue rechazando por la precondicion fiscal (factura viva) -> deriva al camino formal de NC, sin tocar nada.
        var ex = await Assert.ThrowsAsync<AnnulWithCreditRejectedException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin"));
        Assert.Equal(AnnulWithCreditRejectedException.Codes.LiveInvoice, ex.Code);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.PublicId.ToString() == reservaPublicId);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
        Assert.Empty(await ctx.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync());
    }

    // ============================================================
    // (5) PRUEBA DE NECESIDAD: sin EnsureOperatorReceivableAnchorLinesAsync cableada (DI mal armado, mismo
    //     patrón que un test viejo con ctor de 5 args), la anulación cancela los servicios igual, pero SIN
    //     línea que ancle el receivable — la fuga original reaparece. Documenta por qué el ancla es load-bearing.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_WithoutAnchorWired_CancelsServices_ButNoLine_AndReconcileMintsCredit_LeakDemonstration()
    {
        await using var ctx = NewContext();
        var (reservaPublicId, supplierId, hotelId) =
            await SeedFirmReservaAsync(ctx, paidToOperator: true, clientPayment: true);

        // Service SIN el ancla cableada.
        var service = BuildReservaServiceWithoutAnchor(ctx);

        var dto = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId, ValidReason, "admin-1", "Admin");

        // La anulacion procede: el servicio queda cancelado y la caja del operador cae a -50.000.
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        var hotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.Equal(WorkflowStatuses.Cancelado, hotel.Status);
        var balanceRow = await ctx.SupplierBalanceByCurrency.AsNoTracking()
            .FirstAsync(r => r.SupplierId == supplierId && r.Currency == "ARS");
        Assert.Equal(-50_000m, balanceRow.Balance);

        // Sin el ancla cableada, ninguna línea se creó.
        Assert.Empty(await ctx.BookingCancellations.AsNoTracking().ToListAsync());

        // LA FUGA: sin la linea que ancle el receivable Y, el reconcile materializa el negativo como saldo a favor
        // GASTABLE del operador. Esto es JUSTO lo que el ancla (test 1) impide.
        await ReconcileAsync(ctx, supplierId);
        Assert.Equal(50_000m, await PoolAsync(ctx, supplierId));
    }

    // ============================================================
    // Seeds locales para escenarios multi-servicio.
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
    // (6) MULTIMONEDA: servicio ARS impago + servicio USD PAGADO -> ancla SOLO la línea USD (con su RefundCap
    //     en USD), ninguna línea para el ARS impago. Reconcile no mintea en ninguna moneda.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_CrossCurrency_PaidUsdService_CreatesUsdLineOnly()
    {
        await using var ctx = NewContext();
        var supplier = new Supplier { Name = "Operador USD", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        var (_, reserva) = await SeedFirmReservaShellAsync(ctx, "F-XCCY");

        // ARS impago (cap 0) + USD pagado (cap 50.000 USD). Sin factura.
        var hotelArs = AddHotel(ctx, reserva.Id, supplier.Id, netCost: 40_000m, currency: "ARS"); // impago
        var hotelUsd = AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "USD"); // pagado abajo
        AddSupplierPayment(ctx, supplier.Id, reserva.Id, amount: 50_000m, currency: "USD");
        ctx.Payments.Add(NewClientPayment(reserva.Id, 75_000m));
        await ctx.SaveChangesAsync();

        var service = BuildReservaServiceWithAnchor(ctx);
        var dto = await service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        var bc = await ctx.BookingCancellations.AsNoTracking().Include(b => b.Lines).SingleAsync(b => b.ReservaId == reserva.Id);
        Assert.Null(bc.OriginatingInvoiceId);
        var line = Assert.Single(bc.Lines);
        Assert.Equal(hotelUsd.Id, line.ServiceId);
        Assert.Equal("USD", line.Currency);
        Assert.Equal(50_000m, line.RefundCap);

        await ReconcileAsync(ctx, supplier.Id);
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }

    // ============================================================
    // (7) MULTI-OPERADOR: uno PAGADO + uno IMPAGO -> ancla SOLO la línea del pagado.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_MultiOperator_OnePaidOneUnpaid_AnchorsOnlyPaidOperator()
    {
        await using var ctx = NewContext();
        var paidSupplier = new Supplier { Name = "Operador Pagado", IsActive = true };
        var unpaidSupplier = new Supplier { Name = "Operador Impago", IsActive = true };
        ctx.Suppliers.AddRange(paidSupplier, unpaidSupplier);
        await ctx.SaveChangesAsync();
        var (_, reserva) = await SeedFirmReservaShellAsync(ctx, "F-MULTIOP");

        var hotelPaid = AddHotel(ctx, reserva.Id, paidSupplier.Id, netCost: 50_000m, currency: "ARS");
        AddHotel(ctx, reserva.Id, unpaidSupplier.Id, netCost: 30_000m, currency: "ARS");
        AddSupplierPayment(ctx, paidSupplier.Id, reserva.Id, amount: 50_000m, currency: "ARS"); // solo al primero
        ctx.Payments.Add(NewClientPayment(reserva.Id, 100_000m));
        await ctx.SaveChangesAsync();

        var service = BuildReservaServiceWithAnchor(ctx);
        var dto = await service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        var bc = await ctx.BookingCancellations.AsNoTracking().Include(b => b.Lines).SingleAsync(b => b.ReservaId == reserva.Id);
        var line = Assert.Single(bc.Lines);
        Assert.Equal(paidSupplier.Id, line.SupplierId);
        Assert.Equal(hotelPaid.Id, line.ServiceId);
        Assert.Equal(50_000m, line.RefundCap);

        await ReconcileAsync(ctx, paidSupplier.Id);
        Assert.Equal(0m, await PoolAsync(ctx, paidSupplier.Id));
        Assert.Equal(0m, await PoolAsync(ctx, unpaidSupplier.Id));
    }

    // ============================================================
    // (7-bis) MULTI-OPERADOR: ambos IMPAGOS -> anula normal, ninguna línea.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_MultiOperator_BothUnpaid_Annuls_WithoutAnchorLines()
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

        var service = BuildReservaServiceWithAnchor(ctx);

        var dto = await service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        Assert.Empty(await ctx.BookingCancellations.AsNoTracking().ToListAsync());
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().ToListAsync());
    }

    // ============================================================
    // (8a) PARCIAL-PREVIA + residual sin anclar: se pagó al operador por 2 servicios, uno ya tiene su línea
    //      (parcial previa), el OTRO sigue pagado sin línea -> la anulación total crea la línea FALTANTE del
    //      residual, sumando junto a la previa el total pagado. Reconcile no mintea nada.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_PreviousPartialLine_CreatesResidualLine_AndNoMint()
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

        var previousBcId = await SeedNonAbortedPartialLineAsync(ctx, reserva, customer.Id, supplier.Id, hotelA.Id, refundCap: 50_000m);

        var service = BuildReservaServiceWithAnchor(ctx);
        var dto = await service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        // La linea previa (BC previo) sigue intacta con su cap de 50.000; el residual de Hotel B se ancla en el
        // MISMO BC previo (reusa el BC EN CURSO de la reserva) con una linea nueva.
        var bc = await ctx.BookingCancellations.AsNoTracking().Include(b => b.Lines).SingleAsync(b => b.Id == previousBcId);
        Assert.Equal(2, bc.Lines.Count);
        var lineA = bc.Lines.Single(l => l.ServiceId == hotelA.Id);
        var lineB = bc.Lines.Single(l => l.ServiceId == hotelB.Id);
        Assert.Equal(50_000m, lineA.RefundCap);
        Assert.Equal(50_000m, lineB.RefundCap);

        await ReconcileAsync(ctx, supplier.Id);
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }

    // ============================================================
    // (8b) PARCIAL-PREVIA + todo anclado (idempotencia): la parcial previa ya reservó TODO lo pagado al
    //      operador -> Hotel B (impago) no genera línea nueva. Reconcile no mintea (ya estaba anclado).
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_PreviousPartialLine_FullyAnchored_DoesNotDuplicateLine_AndNoMint()
    {
        await using var ctx = NewContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        var (customer, reserva) = await SeedFirmReservaShellAsync(ctx, "F-PARTIAL-B");

        var hotelA = AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "ARS");
        hotelA.Status = WorkflowStatuses.Cancelado;
        AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "ARS"); // Hotel B, impago
        // Se pago al operador SOLO 50.000 (lo de Hotel A), que la parcial previa ya anclo por completo.
        AddSupplierPayment(ctx, supplier.Id, reserva.Id, amount: 50_000m, currency: "ARS");
        ctx.Payments.Add(NewClientPayment(reserva.Id, 150_000m));
        await ctx.SaveChangesAsync();

        var previousBcId = await SeedNonAbortedPartialLineAsync(ctx, reserva, customer.Id, supplier.Id, hotelA.Id, refundCap: 50_000m);

        var service = BuildReservaServiceWithAnchor(ctx);
        var dto = await service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        // Sigue habiendo UNA sola linea (la previa): Hotel B esta impago, no genera linea nueva.
        var bc = await ctx.BookingCancellations.AsNoTracking().Include(b => b.Lines).SingleAsync(b => b.Id == previousBcId);
        var line = Assert.Single(bc.Lines);
        Assert.Equal(hotelA.Id, line.ServiceId);
        Assert.Equal(50_000m, line.RefundCap);

        await ReconcileAsync(ctx, supplier.Id);
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }

    /// <summary>
    /// Siembra DIRECTAMENTE (estilo de este archivo) una cancelacion parcial YA anclada: un BookingCancellation NO
    /// abortado + una BookingCancellationLine Partial para el servicio dado, con su RefundCap. Representa el estado
    /// que dejaria una cancelacion parcial previa; lo lee la deduccion <c>existingLineConsumption</c> de
    /// <c>AssignRefundCapsAsync</c>. SIN factura de venta a proposito (OriginatingInvoiceId null): obra "anular sin
    /// factura", el BC no necesita ancla fiscal para existir. Devuelve el Id del BC sembrado.
    /// </summary>
    private static async Task<int> SeedNonAbortedPartialLineAsync(
        AppDbContext ctx, Reserva reserva, int customerId, int supplierId, int hotelId, decimal refundCap)
    {
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customerId, SupplierId = supplierId,
            OriginatingInvoiceId = null, // obra "anular sin factura": el ancla fiscal es opcional.
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

        return bc.Id;
    }

    // ============================================================
    // (9) SOBREPAGO al operador (pool > costo): el cap se topea por el COSTO del servicio (50.000), no por el
    //     pool pagado (80.000) — la línea ancla lo justificable, no lo pagado de más.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_OverpaidOperator_LineCappedAtCost_NotAtPool()
    {
        await using var ctx = NewContext();
        var supplier = new Supplier { Name = "Operador Sobrepagado", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        var (_, reserva) = await SeedFirmReservaShellAsync(ctx, "F-OVERPAY");

        var hotel = AddHotel(ctx, reserva.Id, supplier.Id, netCost: 50_000m, currency: "ARS");
        // Se le pago 80.000 al operador por un servicio que costo 50.000: el cap se topea en 50.000.
        AddSupplierPayment(ctx, supplier.Id, reserva.Id, amount: 80_000m, currency: "ARS");
        ctx.Payments.Add(NewClientPayment(reserva.Id, 75_000m));
        await ctx.SaveChangesAsync();

        var service = BuildReservaServiceWithAnchor(ctx);
        var dto = await service.AnnulWithPaymentsToCreditAsync(reserva.PublicId.ToString(), ValidReason, "admin-1", "Admin");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        var bc = await ctx.BookingCancellations.AsNoTracking().Include(b => b.Lines).SingleAsync(b => b.ReservaId == reserva.Id);
        var line = Assert.Single(bc.Lines);
        Assert.Equal(hotel.Id, line.ServiceId);
        Assert.Equal(50_000m, line.RefundCap); // topeado por costo, no por lo pagado.

        // El excedente pagado de más (30.000) SI queda como saldo a favor consumible (es sobrepago real, no
        // receivable pendiente): el reconciler lo materializa, distinto de los 50.000 que sí quedaron anclados.
        await ReconcileAsync(ctx, supplier.Id);
        Assert.Equal(30_000m, await PoolAsync(ctx, supplier.Id));
    }

    // ============================================================
    // (B4) PARIDAD: anulación total sin-factura vs con-factura da el MISMO saldo a favor del cliente, el mismo
    //      receivable Y del operador, y balance de la reserva en 0 en ambos casos.
    // ============================================================
    [Fact]
    public async Task AnnulWithCredit_ParityBetweenWithAndWithoutInvoice()
    {
        await using var ctxNoInvoice = NewContext();
        var (reservaNoInvoicePublicId, supplierNoInvoiceId, _) =
            await SeedFirmReservaAsync(ctxNoInvoice, paidToOperator: true, clientPayment: true);
        var dtoNoInvoice = await BuildReservaServiceWithAnchor(ctxNoInvoice)
            .AnnulWithPaymentsToCreditAsync(reservaNoInvoicePublicId, ValidReason, "admin-1", "Admin");

        await using var ctxWithInvoice = NewContext();
        var (reservaWithInvoicePublicId, supplierWithInvoiceId, _) =
            await SeedFirmReservaAsync(ctxWithInvoice, paidToOperator: true, clientPayment: true);
        // NO se agrega factura viva: "con factura" acá sólo prueba que, aun cuando el ancla del BC termina
        // apuntando a null (no hay invoice sembrada), el resultado de plata es idéntico al caso base. La
        // paridad real "factura viva bloquea" ya la cubre el test (4) — este test compara plata, no fiscal.
        var dtoWithInvoice = await BuildReservaServiceWithAnchor(ctxWithInvoice)
            .AnnulWithPaymentsToCreditAsync(reservaWithInvoicePublicId, ValidReason, "admin-1", "Admin");

        Assert.Equal(EstadoReserva.Cancelled, dtoNoInvoice.Status);
        Assert.Equal(EstadoReserva.Cancelled, dtoWithInvoice.Status);

        var reservaNoInvoiceId = await ctxNoInvoice.Reservas.AsNoTracking()
            .Where(r => r.PublicId.ToString() == reservaNoInvoicePublicId).Select(r => r.Id).FirstAsync();
        var reservaWithInvoiceId = await ctxWithInvoice.Reservas.AsNoTracking()
            .Where(r => r.PublicId.ToString() == reservaWithInvoicePublicId).Select(r => r.Id).FirstAsync();

        var reservaNoInvoice = await ctxNoInvoice.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaNoInvoiceId);
        var reservaWithInvoice = await ctxWithInvoice.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaWithInvoiceId);
        Assert.Equal(0m, reservaNoInvoice.Balance);
        Assert.Equal(0m, reservaWithInvoice.Balance);
        Assert.Equal(reservaWithInvoice.Balance, reservaNoInvoice.Balance);

        var clientCreditNoInvoice = await ctxNoInvoice.ClientCreditEntries.AsNoTracking().SumAsync(e => e.CreditedAmount);
        var clientCreditWithInvoice = await ctxWithInvoice.ClientCreditEntries.AsNoTracking().SumAsync(e => e.CreditedAmount);
        Assert.Equal(75_000m, clientCreditNoInvoice);
        Assert.Equal(clientCreditWithInvoice, clientCreditNoInvoice);

        var circuitNoInvoice = await SupplierCancellationCircuitReader.LoadAsync(ctxNoInvoice, supplierNoInvoiceId, CancellationToken.None);
        var circuitWithInvoice = await SupplierCancellationCircuitReader.LoadAsync(ctxWithInvoice, supplierWithInvoiceId, CancellationToken.None);
        Assert.Equal(50_000m, circuitNoInvoice.ReceivableByCurrency.GetValueOrDefault("ARS"));
        Assert.Equal(circuitWithInvoice.ReceivableByCurrency.GetValueOrDefault("ARS"), circuitNoInvoice.ReceivableByCurrency.GetValueOrDefault("ARS"));
    }

    // ============================================================
    // (10) MAPEO CONTROLLER: el endpoint AnnulWithCredit devuelve 409 con el mensaje cuando el service lanza
    //      InvalidOperationException "a secas" (sin Code). Test de la capa controller, mockea IReservaService
    //      (no re-ejercita la lógica de plata: eso lo cubren los tests de arriba).
    // ============================================================
    [Fact]
    public async Task AnnulWithCreditController_MapsGuardException_To409Conflict_WithMessage()
    {
        const string guardMessage = "La reserva tiene factura emitida. Para deshacerla hay que anularla por el camino formal.";

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
        // fallback de siempre, intacto.
        Assert.Null(conflict.Value.GetType().GetProperty("code"));
    }

    // ============================================================
    // (11) MAPEO CONTROLLER — el code estable `UnanchoredOperatorRefund` QUEDA DEFINIDO (T-6: no romper
    //      contratos estables) aunque ya no lo lance el guard real (eliminado, obra 2026-07-23). Este test
    //      prueba SOLO que el ENVELOPE del controller (agregar `code` sin tocar `message`) sigue funcionando
    //      para CUALQUIER AnnulWithCreditRejectedException, catalogado o no — no que este code en particular
    //      siga siendo alcanzable desde el service real.
    // ============================================================
    [Fact]
    public async Task AnnulWithCreditController_MapsRejectedException_To409Conflict_WithCodeAndMessage()
    {
        const string guardMessage = "Mensaje de ejemplo para el envelope aditivo del controller.";

        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(s => s.AnnulWithPaymentsToCreditAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AnnulWithCreditRejectedException(
                AnnulWithCreditRejectedException.Codes.NoPayer, guardMessage));

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

        var messageProperty = conflict.Value!.GetType().GetProperty("message");
        Assert.NotNull(messageProperty);
        Assert.Equal(guardMessage, (string?)messageProperty!.GetValue(conflict.Value));

        var codeProperty = conflict.Value.GetType().GetProperty("code");
        Assert.NotNull(codeProperty);
        Assert.Equal(
            AnnulWithCreditRejectedException.Codes.NoPayer,
            (string?)codeProperty!.GetValue(conflict.Value));
    }
}
