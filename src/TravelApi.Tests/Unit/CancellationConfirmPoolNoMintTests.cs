using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Pasos B/C — BLOQUEANTE de raiz (plata viva, 2026-06-29): el reconciler del pool NO debe mintear el pagado como
/// saldo a favor en el HAPPY-PATH de una anulacion prepaga. Estos tests pasan por el PATH REAL
/// <see cref="BookingCancellationService.ConfirmAsync"/> (no construyen estados directos), porque el bug vivia
/// justo ahi: <c>ConfirmAsync</c> cancela los servicios (caja = -pagado) y dispara el reconcile EN LA MISMA
/// transaccion mientras la BC esta en <c>AwaitingFiscalConfirmation</c> (estado 1). Si ese estado no cuenta el
/// receivable Y, la formula mintea el pagado. Cubrimos tambien la transicion 1->2 (el pool sigue 0, sin chocar
/// INV-SUPCREDIT-001) y la variante durable <c>ArcaRejected</c> (estado 7).
/// </summary>
public class CancellationConfirmPoolNoMintTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"confirm-pool-nomint-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildBcService(AppDbContext ctx, out Mock<IInvoiceService> invoiceMock)
    {
        invoiceMock = new Mock<IInvoiceService>();
        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnablePartialCreditNotes = false,
            EnableCancellationDebitNote = false,
            OperatorRefundTimeoutDays = 60,
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
        };
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        return new BookingCancellationService(
            ctx, invoiceMock.Object, new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object, NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object, new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    private static SupplierService SeeCostSupplierService(AppDbContext ctx)
    {
        const string userId = "tester";
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"))
            }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { Permissions.CobranzasSeeCost, Permissions.TesoreriaSupplierPayments };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);
        return new SupplierService(ctx, auditService: null, httpContextAccessor: accessor, logger: null, permissionResolver: resolver.Object);
    }

    private static ConfirmCancellationRequest NewConfirmRequest() =>
        new(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS", ExchangeRateAtOriginalInvoice: 1m, Source: ExchangeRateSource.BCRA_A3500,
                ManualJustification: null, AgencyTaxConditionAtEvent: "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent: "IVA_RESP_INSCRIPTO", CustomerTaxConditionAtEvent: "CONSUMIDOR_FINAL"),
            IsAdminOverride: false, OverrideReason: null, ApprovalRequestPublicId: null);

    /// <summary>
    /// Siembra una reserva prepaga: hotel confirmado (costo=pagado) + pago al operador + BC en Drafted con su linea
    /// (RefundCap = pagado, como la deja el draft). Devuelve (bc, supplier, reserva).
    /// </summary>
    private static async Task<(BookingCancellation Bc, Supplier Supplier, Reserva Reserva)> SeedPrepaidDraftAsync(
        AppDbContext ctx, decimal paid)
    {
        // Tanda B (2026-07-16): ConfirmAsync resuelve las 3 condiciones fiscales SERVER-SIDE
        // (ResolveServerSideTaxIdentity), no del request.SnapshotData de NewConfirmRequest() (ese
        // campo ahora se ignora). Sin esta fila de AfipSettings, ConfirmAsync rebotaria con INV-118.
        if (!await ctx.AfipSettings.AnyAsync())
        {
            ctx.AfipSettings.Add(new AfipSettings { Cuit = 20111111112, TaxCondition = "Monotributo" });
        }

        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-CONF", Name = "Reserva prepaga", PayerId = customer.Id,
            Status = EstadoReserva.Confirmed, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = paid, SalePrice = paid * 1.5m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        // Pago al operador por el total: tras cancelar el servicio, la caja queda en -paid.
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = paid, Currency = "ARS", Method = "Transferencia"
        });
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 100, CAE = "12345678",
            Resultado = "A", MonId = "PES", ImporteTotal = paid * 1.5m, ImporteNeto = paid * 1.5m,
            ImporteIva = 0m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.Drafted,
            Reason = "Cliente decidio anular el viaje completo", DraftedByUserId = "vendedor-1",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        // Linea del draft: RefundCap = pagado (sin multa). Es lo que deja AssignRefundCapsAsync en el draft.
        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = hotel.Id, Scope = BookingCancellationLineScope.Full, Currency = "ARS",
            LineSaleAmount = paid, RefundCap = paid, ReceivedRefundAmount = 0m,
        });
        await ctx.SaveChangesAsync();

        return (bc, supplier, reserva);
    }

    private static async Task<decimal> PoolAsync(AppDbContext ctx, int supplierId) =>
        (await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplierId).ToListAsync())
            .Sum(e => e.RemainingBalance);

    // ============================================================
    // PATH REAL: ConfirmAsync -> AwaitingFiscalConfirmation -> pool == 0 (NO mintea el pagado)
    // ============================================================
    [Fact]
    public async Task ConfirmAsync_prepaidCancellation_doesNotMintPaidAsCredit()
    {
        await using var ctx = NewContext();
        var service = BuildBcService(ctx, out _);
        var (bc, supplier, _) = await SeedPrepaidDraftAsync(ctx, paid: 50_000m);

        await service.ConfirmAsync(bc.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        // La BC quedo en AwaitingFiscalConfirmation (estado 1) con los servicios cancelados (caja = -50.000).
        var reloadedBc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, reloadedBc.Status);
        Assert.Equal(-50_000m, (await ctx.SupplierBalanceByCurrency.AsNoTracking().FirstAsync(r => r.SupplierId == supplier.Id)).Balance);

        // RAIZ: el reconcile que corrio DENTRO de ConfirmAsync NO mintea el pagado. Y cuenta el receivable
        // (estado 1 ya tiene servicios cancelados): -50.000 (caja) + 50.000 (Y) = 0 -> pool 0.
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
        Assert.Empty(await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplier.Id).ToListAsync());

        // El extracto muestra "me tiene que devolver", NO "saldo a favor".
        var statement = await SeeCostSupplierService(ctx).GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);
        var ars = statement.Currencies.Single(c => c.Currency == "ARS");
        Assert.Equal(50_000m, ars.TheyOweMe);
        Assert.Equal(0m, ars.Prepayment);
        Assert.Equal(0m, ars.ITheyOwe);
    }

    // ============================================================
    // Transicion 1 -> 2: el pool sigue 0 y consistente (no choca INV-SUPCREDIT-001)
    // ============================================================
    [Fact]
    public async Task ConfirmThenAdvanceToAwaitingOperatorRefund_poolStaysZero_noInvariantClash()
    {
        await using var ctx = NewContext();
        var service = BuildBcService(ctx, out _);
        var (bc, supplier, _) = await SeedPrepaidDraftAsync(ctx, paid: 50_000m);

        await service.ConfirmAsync(bc.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));

        // Simular el callback de AFIP (CAE OK): 1 -> 2. Reconciliamos de nuevo (lo haria cualquier trigger del
        // operador). El pool debe seguir 0 sin lanzar INV-SUPCREDIT-001 (no hay saldo aplicado que drenar).
        var tracked = await ctx.BookingCancellations.FirstAsync(b => b.Id == bc.Id);
        tracked.Status = BookingCancellationStatus.AwaitingOperatorRefund;
        await ctx.SaveChangesAsync();

        var ex = await Record.ExceptionAsync(() =>
            TravelApi.Infrastructure.Reservations.SupplierCreditReconciler.ReconcileAsync(
                ctx, supplier.Id, sourceSupplierPaymentId: null, actorUserId: null, actorUserName: null,
                auditService: null, CancellationToken.None));

        Assert.Null(ex); // sin choque de invariante
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }

    // ============================================================
    // Variante durable ArcaRejected (estado 7): servicios cancelados, Y cuenta -> pool 0
    // ============================================================
    [Fact]
    public async Task ArcaRejected_servicesCancelled_receivableCounts_poolNotMinted()
    {
        await using var ctx = NewContext();
        var service = BuildBcService(ctx, out _);
        var (bc, supplier, _) = await SeedPrepaidDraftAsync(ctx, paid: 50_000m);

        await service.ConfirmAsync(bc.PublicId, NewConfirmRequest(), "vendedor-1", "Vendedor Uno",
            requesterIsAdmin: false, ct: default);

        // AFIP rechaza la NC: 1 -> 7 (servicios ya cancelados, caja sigue -50.000).
        var tracked = await ctx.BookingCancellations.FirstAsync(b => b.Id == bc.Id);
        tracked.Status = BookingCancellationStatus.ArcaRejected;
        await ctx.SaveChangesAsync();

        await TravelApi.Infrastructure.Reservations.SupplierCreditReconciler.ReconcileAsync(
            ctx, supplier.Id, sourceSupplierPaymentId: null, actorUserId: null, actorUserName: null,
            auditService: null, CancellationToken.None);

        // ArcaRejected NO esta en el deny-set pre-cancelacion -> Y cuenta -> pool 0 (no se mintea durante la
        // remediacion manual, que puede durar dias).
        Assert.Equal(0m, await PoolAsync(ctx, supplier.Id));
    }
}
