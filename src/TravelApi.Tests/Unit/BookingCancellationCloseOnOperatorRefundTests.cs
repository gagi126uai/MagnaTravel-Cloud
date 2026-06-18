using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-033 (2026-06-18): tests unit puros del cierre de la RESERVA cuando el OPERADOR
/// reembolsa el total esperado, via <c>BookingCancellationService.OnAllocationRecordedAsync</c>.
///
/// <para>Antes de ADR-033 la reserva en <c>PendingOperatorRefund</c> solo se cerraba cuando el
/// CLIENTE consumia todo su saldo a favor (<c>OnAllCreditConsumedAsync</c>). Si nunca lo consumia,
/// quedaba colgada para siempre. Ahora se cierra automaticamente cuando todas las lineas con
/// <c>RefundCap &gt; 0</c> quedan <c>Settled</c>.</para>
///
/// <para>InMemory de EF (mismo trade-off que <see cref="BookingCancellationServicePartialCreditNoteTests"/>):
/// no valida CHECK SQL ni xmin, pero alcanza para la logica de transicion + idempotencia.
/// El callback lee las lineas desde el ChangeTracker, que InMemory soporta igual que Postgres.</para>
/// </summary>
public class BookingCancellationCloseOnOperatorRefundTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr033-close-refund-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static (BookingCancellationService Service, Mock<IAuditService> AuditMock) BuildService(AppDbContext ctx)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        var approvalMock = new Mock<IApprovalRequestService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        var adminCountMock = new Mock<IAdminUserCountService>();

        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OperatorRefundTimeoutDays = 60,
            });

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            approvalMock.Object,
            auditMock.Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            calculatorMock.Object,
            adminCountMock.Object);

        return (service, auditMock);
    }

    /// <summary>
    /// Arma una reserva en <c>PendingOperatorRefund</c> con un BC en
    /// <paramref name="bcStatus"/> y las lineas indicadas. Las lineas se persisten con sus
    /// montos ya "imputados" (simulando lo que DistributeReceivedRefundToOperatorLines deja en
    /// memoria justo antes del callback). Devuelve la entidad BC trackeada.
    /// </summary>
    private static async Task<BookingCancellation> SeedAsync(
        AppDbContext ctx,
        IReadOnlyList<(decimal RefundCap, decimal Received, BookingCancellationLineRefundStatus Status)> lines,
        string reservaStatus = EstadoReserva.PendingOperatorRefund,
        BookingCancellationStatus bcStatus = BookingCancellationStatus.ClientCreditApplied)
    {
        var customer = new Customer { FullName = "Cliente ADR-033", IsActive = true };
        var supplier = new Supplier { Name = "Operador ADR-033", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-ADR033",
            Name = "Reserva cancelada esperando refund",
            PayerId = customer.Id,
            Status = reservaStatus,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            Status = bcStatus,
            Reason = "Cancelacion con refund esperado del operador",
            DraftedByUserId = "vendedor-1",
            DraftedByUserName = "Juan Vendedor",
            ReceivedRefundAmount = lines.Sum(l => l.Received),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        foreach (var (refundCap, received, status) in lines)
        {
            ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
            {
                BookingCancellationId = bc.Id,
                SupplierId = supplier.Id,
                ServiceTable = CancellableServiceTable.Hotel,
                ServiceId = 1,
                Scope = BookingCancellationLineScope.Full,
                Currency = Monedas.ARS,
                LineSaleAmount = refundCap,
                RefundCap = refundCap,
                ReceivedRefundAmount = received,
                RefundStatus = status,
            });
        }
        await ctx.SaveChangesAsync();

        return bc;
    }

    // =========================================================================
    // (1) Operador reembolsa el total esperado -> reserva pasa a Cancelled.
    // =========================================================================

    [Fact]
    public async Task OnAllocationRecorded_operadorReembolsaTotal_cierraReserva()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);

        var bc = await SeedAsync(ctx, new[]
        {
            (RefundCap: 1000m, Received: 1000m, Status: BookingCancellationLineRefundStatus.Settled),
        });

        await service.OnAllocationRecordedAsync(bc.Id, netAmount: 1000m, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

        // Rastro aditivo del cambio de estado.
        var log = await ctx.ReservaStatusChangeLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ReservaId == bc.ReservaId
                                   && l.ToStatus == EstadoReserva.Cancelled);
        Assert.NotNull(log);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, log!.FromStatus);
        Assert.Null(log.ByUserId); // cierre del sistema, sin actor humano

        // Audit dedicado de la via "operador reembolso el total".
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationClosedByOperatorRefund,
            AuditActions.BookingCancellationEntityName,
            bc.Id.ToString(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // El BC NO se cierra: el cliente todavia tiene su saldo a favor vivo.
        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.ClientCreditApplied, bcAfter.Status);
    }

    // =========================================================================
    // (2) Operador reembolsa PARCIAL -> reserva sigue en PendingOperatorRefund.
    // =========================================================================

    [Fact]
    public async Task OnAllocationRecorded_reembolsoParcial_noCierraReserva()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);

        // Dos lineas: una Settled, otra todavia pendiente -> NO todas Settled.
        var bc = await SeedAsync(ctx, new[]
        {
            (RefundCap: 1000m, Received: 1000m, Status: BookingCancellationLineRefundStatus.Settled),
            (RefundCap: 1000m, Received: 400m,  Status: BookingCancellationLineRefundStatus.PendingOperatorRefund),
        });

        await service.OnAllocationRecordedAsync(bc.Id, netAmount: 400m, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reserva.Status);

        // No hubo cierre -> no se loguea el audit de cierre por refund.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationClosedByOperatorRefund,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // (3) Idempotencia: si la reserva ya esta Cancelled, no rompe ni re-loguea.
    // =========================================================================

    [Fact]
    public async Task OnAllocationRecorded_reservaYaCancelled_noReCierra()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);

        // La reserva ya fue cerrada (p.ej. el cliente consumio el credito antes).
        var bc = await SeedAsync(ctx, new[]
        {
            (RefundCap: 1000m, Received: 1000m, Status: BookingCancellationLineRefundStatus.Settled),
        }, reservaStatus: EstadoReserva.Cancelled);

        await service.OnAllocationRecordedAsync(bc.Id, netAmount: 1000m, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

        // No debe haber un segundo log de cierre ni audit (guard por estado).
        var closeLogs = await ctx.ReservaStatusChangeLogs.AsNoTracking()
            .Where(l => l.ReservaId == bc.ReservaId && l.ToStatus == EstadoReserva.Cancelled)
            .CountAsync();
        Assert.Equal(0, closeLogs);

        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationClosedByOperatorRefund,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // (4) Sin lineas con RefundCap > 0 -> no cierra por esta via.
    // =========================================================================

    [Fact]
    public async Task OnAllocationRecorded_sinLineasConRefundCap_noCierraPorEstaVia()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);

        // Linea con RefundCap = 0 (no se esperaba refund del operador).
        var bc = await SeedAsync(ctx, new[]
        {
            (RefundCap: 0m, Received: 0m, Status: BookingCancellationLineRefundStatus.None),
        });

        await service.OnAllocationRecordedAsync(bc.Id, netAmount: 0m, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reserva.Status);

        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationClosedByOperatorRefund,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
