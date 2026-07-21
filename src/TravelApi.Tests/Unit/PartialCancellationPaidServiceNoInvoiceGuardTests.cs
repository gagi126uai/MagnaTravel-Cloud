using System;
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
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// R1 (Pasos B/C, plata viva, 2026-06-30): no se puede cancelar parcialmente un servicio PAGADO al operador
/// (pago IMPUTADO a la reserva) cuando la reserva NO tiene factura de venta viva que ancle el receivable
/// "me tiene que devolver". Sin ese ancla, cancelar dejaria la caja del operador en negativo sin linea que lo
/// represente y el reconciler mintearia ese negativo como saldo a favor (fuga). Hasta tapar R1 con el modelo
/// fiscal (factura como ancla estructural del BookingCancellation), se BLOQUEA con mensaje claro.
///
/// <para><b>Precision del bloqueo</b>: solo aplica al servicio con plata pagada IMPUTADA (RefundCap reconstruido
/// &gt; 0). Un servicio IMPAGO no se bloquea. Un ADVANCE "a cuenta" (no imputado a la reserva) ES saldo a favor
/// genuino, no se bloquea (lo cubre el test existente <c>CancelService_ConfirmedPaidHotel_DropsSupplierDebt_B1</c>).</para>
/// </summary>
public class PartialCancellationPaidServiceNoInvoiceGuardTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"r1-guard-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildBcService(AppDbContext ctx)
    {
        var settings = new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 };
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        return new BookingCancellationService(
            ctx, new Mock<IInvoiceService>().Object, new Mock<IApprovalRequestService>().Object,
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
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"))
            }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new System.Collections.Generic.HashSet<string> { Permissions.CobranzasSeeCost, Permissions.TesoreriaSupplierPayments };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);
        return new SupplierService(ctx, auditService: null, httpContextAccessor: accessor, logger: null, permissionResolver: resolver.Object);
    }

    /// <summary>Reserva Confirmed + hotel Confirmado + (opcional) pago IMPUTADO a la reserva. SIN factura de venta.</summary>
    private static async Task<(Reserva Reserva, Supplier Supplier, HotelBooking Hotel)> SeedAsync(
        AppDbContext ctx, decimal paidImputedToReserva)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-R1", Name = "R-R1", PayerId = customer.Id, Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        if (paidImputedToReserva > 0m)
        {
            // Pago IMPUTADO a la reserva (ReservaId set) -> entra al pool de RefundCap -> receivable real.
            ctx.SupplierPayments.Add(new SupplierPayment
            {
                SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = paidImputedToReserva, Currency = "ARS",
                ImputedCurrency = "ARS", ImputedAmount = paidImputedToReserva, Method = "Transferencia",
            });
            await ctx.SaveChangesAsync();
            await TravelApi.Infrastructure.Reservations.SupplierDebtPersister.PersistAsync(ctx, supplier.Id, CancellationToken.None);
            await ctx.SaveChangesAsync();
        }

        return (reserva, supplier, hotel);
    }

    // ============================================================
    // Servicio PAGADO (imputado) + sin factura -> BLOQUEA (mensaje claro), NO cancela
    // ============================================================
    [Fact]
    public async Task PaidImputedService_noInvoice_partialCancel_isBlocked_serviceStaysAlive()
    {
        await using var ctx = NewContext();
        var (reserva, _, hotel) = await SeedAsync(ctx, paidImputedToReserva: 50_000m);
        var service = BuildBcService(ctx);

        // Tanda 7 "contrato pantalla-motor" (2026-07-20): el candado R1 ahora tira ServiceCancellationRejectedException
        // (mismo InvalidOperationException de siempre + un Code aditivo) para que el frontend pueda ofrecer el
        // boton "Emitir factura" en vez de adivinar el motivo por texto.
        var ex = await Assert.ThrowsAsync<ServiceCancellationRejectedException>(() =>
            service.CancelServiceAsync(
                new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel"),
                "vendedor-1", "Vendedor", CancellationToken.None));

        Assert.Equal(ServiceCancellationRejectedException.Codes.UnanchoredOperatorRefund, ex.Code);

        // Mensaje saneado: habla de facturar/reembolso, sin internals (ids, nombres de clase, RefundCap, etc.).
        Assert.Contains("factura", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RefundCap", ex.Message);
        Assert.DoesNotContain("BookingCancellation", ex.Message);

        // El servicio NO se cancelo (no quedo estado a medias): sigue Confirmado.
        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Confirmado", reloaded.Status);
        // Y no se creo ninguna linea de cancelacion.
        Assert.Empty(await ctx.Set<BookingCancellationLine>().AsNoTracking().ToListAsync());
    }

    // ============================================================
    // Servicio IMPAGO + sin factura -> NO se bloquea (no hay receivable que anclar)
    // ============================================================
    [Fact]
    public async Task UnpaidService_noInvoice_partialCancel_proceeds()
    {
        await using var ctx = NewContext();
        var (reserva, _, hotel) = await SeedAsync(ctx, paidImputedToReserva: 0m);
        var service = BuildBcService(ctx);

        var result = await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel impago"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        Assert.Equal(1, result.CancelledServicesCount);
        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Cancelado", reloaded.Status);
    }

    // ============================================================
    // Tras el bloqueo, un pago al operador NO mintea (el servicio sigue vivo, su compra respalda el pago)
    // ============================================================
    [Fact]
    public async Task AfterBlock_supplierPaymentTrigger_doesNotMint()
    {
        await using var ctx = NewContext();
        var (reserva, supplier, hotel) = await SeedAsync(ctx, paidImputedToReserva: 50_000m);
        var bcService = BuildBcService(ctx);

        // El intento de cancelar el servicio pagado sin factura se bloquea (el servicio queda vivo). Tipo
        // EXACTO (Assert.ThrowsAsync no reconoce herencia): ServiceCancellationRejectedException.
        await Assert.ThrowsAsync<ServiceCancellationRejectedException>(() =>
            bcService.CancelServiceAsync(
                new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "intento"),
                "vendedor-1", "Vendedor", CancellationToken.None));

        // Un pago al operador (trigger real del reconcile): la compra confirmada (hotel vivo) respalda el pago,
        // balance = compras 50.000 - pagos 50.000 = 0, sin sobrepago -> pool 0. No hay fuga porque NO se cancelo.
        var supplierService = SeeCostSupplierService(ctx);
        var paymentRequest = new SupplierPaymentRequest(
            Amount: 10_000m, Method: "T", Reference: null, Notes: null, ReservaId: null,
            ServicioReservaId: null, IsAdvanceToAccount: true, Currency: "ARS");
        await supplierService.AddSupplierPaymentAsync(supplier.Id, paymentRequest, CancellationToken.None);

        // El unico saldo a favor posible es el advance recien hecho (10.000), NO el pagado del servicio vivo.
        var pool = (await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplier.Id).ToListAsync())
            .Sum(e => e.RemainingBalance);
        Assert.Equal(10_000m, pool); // solo el advance legitimo; el servicio pagado NO se minteo (sigue vivo)
    }
}
