using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
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
/// (2026-06-26) AGUJERO 2 — cierre del ciclo del reembolso del operador.
///
/// <para>Antes, <see cref="BookingCancellationStatus.AbandonedByOperator"/> nunca se asignaba (codigo muerto) y no
/// habia job que mirara <see cref="BookingCancellation.OperatorRefundDueBy"/>: cuando el operador no devolvia el
/// reembolso, la cuenta por cobrar quedaba colgada para siempre. Ahora
/// <see cref="BookingCancellationService.ProcessExpiredOperatorRefundsAsync"/> (que invoca el job nocturno
/// <see cref="OperatorRefundTimeoutJob"/>) transiciona las vencidas a <c>AbandonedByOperator</c> y cierra la
/// reserva. Tests: vencida -> abandonada; con plazo futuro -> intacta; idempotente; el job lleva el guard de
/// concurrencia.</para>
/// </summary>
public class OperatorRefundTimeoutTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"operator-refund-timeout-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (BookingCancellationService Service, Mock<IAuditService> AuditMock) BuildService(AppDbContext ctx)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        var approvalMock = new Mock<IApprovalRequestService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        var adminCountMock = new Mock<IAdminUserCountService>();

        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalMock.Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            calculatorMock.Object, adminCountMock.Object);

        return (service, auditMock);
    }

    /// <summary>
    /// Arma una reserva en <paramref name="reservaStatus"/> con un BC en <paramref name="bcStatus"/> y el plazo
    /// de reembolso indicado. Devuelve el BC trackeado.
    /// </summary>
    private static async Task<BookingCancellation> SeedAsync(
        AppDbContext ctx,
        DateTime? operatorRefundDueBy,
        string reservaStatus = EstadoReserva.PendingOperatorRefund,
        BookingCancellationStatus bcStatus = BookingCancellationStatus.AwaitingOperatorRefund)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-TIMEOUT",
            Name = "Reserva esperando refund del operador",
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
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-90),
            OperatorRefundDueBy = operatorRefundDueBy,
            EstimatedRefundAmount = 1000m,
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return bc;
    }

    [Fact]
    public async Task PastDue_AwaitingOperatorRefund_TransitionsToAbandonedAndClosesReserva()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);
        var bc = await SeedAsync(ctx, operatorRefundDueBy: DateTime.UtcNow.AddDays(-1));

        var count = await service.ProcessExpiredOperatorRefundsAsync(CancellationToken.None);

        Assert.Equal(1, count);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AbandonedByOperator, bcAfter.Status);
        Assert.NotNull(bcAfter.ClosedAt);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

        // Rastro auditable del cierre de la reserva (cambio de estado por el sistema).
        var log = await ctx.ReservaStatusChangeLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ReservaId == bc.ReservaId && l.ToStatus == EstadoReserva.Cancelled);
        Assert.NotNull(log);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, log!.FromStatus);
        Assert.Null(log.ByUserId); // transicion del sistema, sin actor humano

        // Audit dedicado del abandono por timeout.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationAbandonedByOperator,
            AuditActions.BookingCancellationEntityName,
            bc.Id.ToString(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FutureDue_IsNotTouched()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);
        var bc = await SeedAsync(ctx, operatorRefundDueBy: DateTime.UtcNow.AddDays(+5));

        var count = await service.ProcessExpiredOperatorRefundsAsync(CancellationToken.None);

        Assert.Equal(0, count);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcAfter.Status); // intacta
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reserva.Status);

        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationAbandonedByOperator,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NullDueDate_IsNotTouched()
    {
        // Una cancelacion sin plazo seteado no se abandona (no hay vencimiento que medir).
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        var bc = await SeedAsync(ctx, operatorRefundDueBy: null);

        var count = await service.ProcessExpiredOperatorRefundsAsync(CancellationToken.None);

        Assert.Equal(0, count);
        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcAfter.Status);
    }

    [Fact]
    public async Task Idempotent_SecondRun_DoesNothing()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);
        var bc = await SeedAsync(ctx, operatorRefundDueBy: DateTime.UtcNow.AddDays(-1));

        var first = await service.ProcessExpiredOperatorRefundsAsync(CancellationToken.None);
        var second = await service.ProcessExpiredOperatorRefundsAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // ya no esta en AwaitingOperatorRefund -> no se reprocesa

        // Solo UN log de cierre y UN audit (no se duplican al re-correr).
        var closeLogs = await ctx.ReservaStatusChangeLogs.AsNoTracking()
            .CountAsync(l => l.ReservaId == bc.ReservaId && l.ToStatus == EstadoReserva.Cancelled);
        Assert.Equal(1, closeLogs);

        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationAbandonedByOperator,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PastDue_ReservaAlreadyCancelled_AbandonsBcWithoutReMovingReserva()
    {
        // Guard de idempotencia: si la reserva YA quedo Cancelled por otra via (ej. el cliente consumio su credito
        // antes), abandonar la BC NO re-mueve la reserva ni vuelve a loguear su cambio de estado.
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        var bc = await SeedAsync(ctx, operatorRefundDueBy: DateTime.UtcNow.AddDays(-1),
            reservaStatus: EstadoReserva.Cancelled);

        var count = await service.ProcessExpiredOperatorRefundsAsync(CancellationToken.None);

        Assert.Equal(1, count);
        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AbandonedByOperator, bcAfter.Status); // la BC SI se abandona

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status); // sigue Cancelled, no se rompio

        // No se logueo un cambio de estado de la reserva: el guard por estado lo evita (ya estaba Cancelled).
        var logs = await ctx.ReservaStatusChangeLogs.AsNoTracking()
            .CountAsync(l => l.ReservaId == bc.ReservaId && l.ToStatus == EstadoReserva.Cancelled);
        Assert.Equal(0, logs);
    }

    [Fact]
    public async Task PoisonRow_OneCancellationFails_OthersStillProcessed()
    {
        // Aislamiento de fila veneno: una cancelacion que falla al procesarse NO frena a las demas.
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);
        var bc1 = await SeedAsync(ctx, operatorRefundDueBy: DateTime.UtcNow.AddDays(-2));
        var bc2 = await SeedAsync(ctx, operatorRefundDueBy: DateTime.UtcNow.AddDays(-1));

        // La auditoria de bc1 EXPLOTA (simula la fila veneno): su procesamiento se saltea sin tumbar a bc2.
        auditMock.Setup(a => a.LogBusinessEventAsync(
                It.IsAny<string>(), It.IsAny<string>(), bc1.Id.ToString(),
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("poison row"));

        var count = await service.ProcessExpiredOperatorRefundsAsync(CancellationToken.None);

        Assert.Equal(1, count); // solo bc2 se abandono

        var bc1After = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc1.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc1After.Status); // se salteo (sin persistir)

        var bc2After = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc2.Id);
        Assert.Equal(BookingCancellationStatus.AbandonedByOperator, bc2After.Status); // se proceso igual
    }

    [Fact]
    public async Task JobRunAsync_DelegatesToService()
    {
        // El job es un envoltorio fino: delega en el service de dominio. Verificamos que lo invoca.
        var serviceMock = new Mock<IBookingCancellationService>();
        serviceMock.Setup(s => s.ProcessExpiredOperatorRefundsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var job = new OperatorRefundTimeoutJob(serviceMock.Object, NullLogger<OperatorRefundTimeoutJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        serviceMock.Verify(s => s.ProcessExpiredOperatorRefundsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void JobRunAsync_HasDisableConcurrentExecutionGuard()
    {
        // Sin el guard, una corrida programada y un reintento podrian solaparse y doble-procesar.
        var method = typeof(OperatorRefundTimeoutJob).GetMethod(nameof(OperatorRefundTimeoutJob.RunAsync))!;
        var attr = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(attr);
    }
}
