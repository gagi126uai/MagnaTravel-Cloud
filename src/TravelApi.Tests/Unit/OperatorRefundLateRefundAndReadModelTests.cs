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
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-041 TANDA 4 (2026-06-28): "esperando reembolso" del operador (read-model) + reembolso TARDIO.
///
/// <para>Tests UNIT con EF InMemory (sin Docker). Cubren la LOGICA: la transicion controlada de reapertura por
/// reembolso tardio (<c>AbandonedByOperator</c> -> <c>AwaitingOperatorRefund</c>), su idempotencia, sus rechazos,
/// y el read-model de reembolsos pendientes (semaforo, filtro por operador, masking de montos de costo). InMemory
/// NO valida CHECK constraints ni xmin: el cuadre real queda para integracion. Una vez reabierta, la imputacion
/// del ingreso (saldo a favor del cliente) la cubre el circuito normal de <c>OperatorRefundService.AllocateAsync</c>
/// (ya testeado en integracion para el estado AwaitingOperatorRefund).</para>
/// </summary>
public class OperatorRefundLateRefundAndReadModelTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"late-refund-{Guid.NewGuid()}")
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
    /// Siembra una reserva + cancelacion con su factura de origen (MapToDtoAsync la requiere) en el estado dado.
    /// Devuelve el BC trackeado.
    /// </summary>
    private static async Task<BookingCancellation> SeedCancellationAsync(
        AppDbContext ctx,
        BookingCancellationStatus bcStatus,
        string reservaStatus = EstadoReserva.Cancelled,
        DateTime? operatorRefundDueBy = null,
        DateTime? closedAt = null)
    {
        var customer = new Customer { FullName = "Cliente Tardio", IsActive = true };
        var supplier = new Supplier { Name = "Operador Tardio", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-LATE",
            Name = "Reserva con reembolso tardio",
            PayerId = customer.Id,
            Status = reservaStatus,
        };
        ctx.Reservas.Add(reserva);

        // MapToDtoAsync accede a bc.OriginatingInvoice.PublicId sin null-check: necesitamos una factura origen.
        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = bcStatus,
            Reason = "Cancelacion con refund esperado del operador",
            DraftedByUserId = "vendedor-1",
            DraftedByUserName = "Juan Vendedor",
            OperatorRefundDueBy = operatorRefundDueBy,
            ClosedAt = closedAt,
            EstimatedRefundAmount = 1000m,
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return bc;
    }

    // =====================================================================================
    // Reembolso tardio: reapertura controlada
    // =====================================================================================

    [Fact]
    public async Task ReopenForLateRefund_fromAbandoned_reopensAndExtendsDueByAndKeepsReservaCancelled()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);
        var bc = await SeedCancellationAsync(ctx,
            BookingCancellationStatus.AbandonedByOperator,
            reservaStatus: EstadoReserva.Cancelled,
            operatorRefundDueBy: DateTime.UtcNow.AddDays(-90),
            closedAt: DateTime.UtcNow.AddDays(-30));

        var dto = await service.ReopenAbandonedForLateRefundAsync(
            bc.PublicId, "El operador devolvio la plata fuera de plazo", "cajero-1", "Cajero Uno", CancellationToken.None);

        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund.ToString(), dto.Status);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcAfter.Status);
        Assert.Null(bcAfter.ClosedAt);                            // se limpio el cierre
        Assert.NotNull(bcAfter.OperatorRefundDueBy);
        Assert.True(bcAfter.OperatorRefundDueBy > DateTime.UtcNow); // plazo nuevo en el futuro (no lo re-abandona el job)

        // La reserva NO se resucita: el viaje sigue cancelado.
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

        // Marca durable del "tardio" = el audit dedicado.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationReopenedForLateRefund,
            AuditActions.BookingCancellationEntityName,
            bc.Id.ToString(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReopenForLateRefund_alreadyAwaiting_isNoOpWithoutAudit()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);
        var bc = await SeedCancellationAsync(ctx,
            BookingCancellationStatus.AwaitingOperatorRefund,
            operatorRefundDueBy: DateTime.UtcNow.AddDays(5));

        var dto = await service.ReopenAbandonedForLateRefundAsync(
            bc.PublicId, "Intento de reapertura sobre una ya abierta", "cajero-1", "Cajero Uno", CancellationToken.None);

        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund.ToString(), dto.Status);

        // No se re-audita una reapertura que no ocurrio.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationReopenedForLateRefund,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReopenForLateRefund_fromClosed_isRejected()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        // Closed SIN lineas -> receivable $0 -> no reabrible (sigue rechazando).
        var bc = await SeedCancellationAsync(ctx, BookingCancellationStatus.Closed);

        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ReopenAbandonedForLateRefundAsync(
                bc.PublicId, "No deberia poder reabrirse desde Closed sin residuo", "cajero-1", null, CancellationToken.None));
    }

    // =====================================================================================
    // FIX A (2026-07-04): reembolso tardio contra una anulacion CERRADA CON RESIDUO
    // =====================================================================================

    /// <summary>Agrega una linea al BC (servicio inexistente -> el circuit reader la cuenta como cancelada).</summary>
    private static async Task AddLineWithResidueAsync(
        AppDbContext ctx, BookingCancellation bc, decimal cap, decimal received)
    {
        ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = bc.SupplierId,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 987654, // no existe HotelBooking con este Id -> servicio cancelado -> residuo cuenta
            Scope = BookingCancellationLineScope.Full,
            Currency = "ARS",
            LineSaleAmount = cap,
            RefundCap = cap,
            ReceivedRefundAmount = received,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task ReopenForLateRefund_fromClosedWithResidue_reopensAndKeepsReservaCancelled()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);
        var bc = await SeedCancellationAsync(ctx,
            BookingCancellationStatus.Closed,
            reservaStatus: EstadoReserva.Cancelled,
            closedAt: DateTime.UtcNow.AddDays(-10));
        // El operador reembolso de menos: cap 1000, recibido 300 -> residuo vivo 700.
        await AddLineWithResidueAsync(ctx, bc, cap: 1000m, received: 300m);

        var dto = await service.ReopenAbandonedForLateRefundAsync(
            bc.PublicId, "El operador devolvio el resto fuera de plazo", "cajero-1", "Cajero Uno", CancellationToken.None);

        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund.ToString(), dto.Status);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcAfter.Status);
        Assert.Null(bcAfter.ClosedAt);                              // se limpio el cierre
        Assert.True(bcAfter.OperatorRefundDueBy > DateTime.UtcNow); // plazo nuevo en el futuro

        // La reserva NO se resucita: el viaje sigue cancelado.
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

        // Auditoria del evento de reapertura (distinguible por previousStatus = Closed en el detalle).
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationReopenedForLateRefund,
            AuditActions.BookingCancellationEntityName,
            bc.Id.ToString(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReopenForLateRefund_fromClosedFullyRefunded_isRejected()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        var bc = await SeedCancellationAsync(ctx, BookingCancellationStatus.Closed);
        // Sin residuo: cap 500, recibido 500 -> receivable $0 -> no reabrible.
        await AddLineWithResidueAsync(ctx, bc, cap: 500m, received: 500m);

        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ReopenAbandonedForLateRefundAsync(
                bc.PublicId, "No hay residuo, no deberia reabrirse", "cajero-1", null, CancellationToken.None));
    }

    [Fact]
    public async Task ReopenForLateRefund_fromClosedWithResidue_thenReadModelShowsRegistrable()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        var bc = await SeedCancellationAsync(ctx, BookingCancellationStatus.Closed, closedAt: DateTime.UtcNow.AddDays(-5));
        await AddLineWithResidueAsync(ctx, bc, cap: 1000m, received: 300m);

        await service.ReopenAbandonedForLateRefundAsync(
            bc.PublicId, "El operador devolvio el resto fuera de plazo", "cajero-1", "Cajero Uno", CancellationToken.None);

        // Tras reabrir, la fila del read-model ya admite el registro directo y ya NO ofrece reabrir.
        var readModel = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var items = await readModel.GetSupplierPendingRefundsAsync(bc.SupplierId, CancellationToken.None);
        var row = items.Single(i => i.BookingCancellationPublicId == bc.PublicId);
        Assert.True(row.CanRegisterRefund);
        Assert.False(row.CanReopenForLateRefund);
    }

    [Fact]
    public async Task ReopenForLateRefund_shortReason_isRejected()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        var bc = await SeedCancellationAsync(ctx, BookingCancellationStatus.AbandonedByOperator,
            operatorRefundDueBy: DateTime.UtcNow.AddDays(-90));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReopenAbandonedForLateRefundAsync(
                bc.PublicId, "corto", "cajero-1", null, CancellationToken.None));
    }

    [Fact]
    public async Task ReopenForLateRefund_unknownBc_throwsNotFound()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ReopenAbandonedForLateRefundAsync(
                Guid.NewGuid(), "Cancelacion inexistente para reabrir", "cajero-1", null, CancellationToken.None));
    }

    // =====================================================================================
    // Semaforo (funcion pura)
    // =====================================================================================

    [Fact]
    public void DeriveSemaphore_coversFourStates()
    {
        var now = new DateTime(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc);

        // Abandonado: el estado manda, sin importar el plazo.
        Assert.Equal(OperatorRefundPendingSemaphore.Abandoned,
            OperatorRefundReadModelService.DeriveSemaphore(
                BookingCancellationStatus.AbandonedByOperator, now.AddDays(10), now));

        // Vencido: plazo en el pasado, todavia esperando.
        Assert.Equal(OperatorRefundPendingSemaphore.Overdue,
            OperatorRefundReadModelService.DeriveSemaphore(
                BookingCancellationStatus.AwaitingOperatorRefund, now.AddDays(-1), now));

        // Por vencer: dentro de la ventana de aviso (7 dias).
        Assert.Equal(OperatorRefundPendingSemaphore.DueSoon,
            OperatorRefundReadModelService.DeriveSemaphore(
                BookingCancellationStatus.AwaitingOperatorRefund, now.AddDays(3), now));

        // A tiempo: plazo lejano.
        Assert.Equal(OperatorRefundPendingSemaphore.OnTime,
            OperatorRefundReadModelService.DeriveSemaphore(
                BookingCancellationStatus.AwaitingOperatorRefund, now.AddDays(30), now));

        // A tiempo: sin plazo.
        Assert.Equal(OperatorRefundPendingSemaphore.OnTime,
            OperatorRefundReadModelService.DeriveSemaphore(
                BookingCancellationStatus.AwaitingOperatorRefund, null, now));
    }

    // =====================================================================================
    // Read-model: filtro por operador, semaforo, masking
    // =====================================================================================

    private static IHttpContextAccessor AdminAccessor()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Role, "Admin") }, authenticationType: "test")),
        };
        return new HttpContextAccessor { HttpContext = http };
    }

    /// <summary>Siembra un escenario multi-cancelacion para el read-model. Devuelve (supplierAId, supplierBId).</summary>
    private static async Task<(int SupplierAId, int SupplierBId)> SeedReadModelAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente RM", IsActive = true };
        var supplierA = new Supplier { Name = "Operador A", IsActive = true };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplierA);
        ctx.Suppliers.Add(supplierB);
        await ctx.SaveChangesAsync();

        async Task<BookingCancellation> AddBcAsync(
            BookingCancellationStatus status, DateTime? dueBy, string numero)
        {
            var reserva = new Reserva { NumeroReserva = numero, Name = numero, PayerId = customer.Id, Status = EstadoReserva.Cancelled };
            ctx.Reservas.Add(reserva);
            var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
            ctx.Invoices.Add(invoice);
            await ctx.SaveChangesAsync();

            var bc = new BookingCancellation
            {
                ReservaId = reserva.Id,
                CustomerId = customer.Id,
                SupplierId = supplierA.Id,
                OriginatingInvoiceId = invoice.Id,
                Status = status,
                Reason = "rm",
                DraftedByUserId = "v",
                OperatorRefundDueBy = dueBy,
            };
            ctx.BookingCancellations.Add(bc);
            await ctx.SaveChangesAsync();
            return bc;
        }

        void AddLine(int bcId, int supplierId, decimal cap, decimal received, string currency = "ARS")
        {
            ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
            {
                BookingCancellationId = bcId,
                SupplierId = supplierId,
                ServiceTable = CancellableServiceTable.Hotel,
                ServiceId = 1,
                Scope = BookingCancellationLineScope.Full,
                Currency = currency,
                LineSaleAmount = cap,
                RefundCap = cap,
                ReceivedRefundAmount = received,
            });
        }

        var onTime = await AddBcAsync(BookingCancellationStatus.AwaitingOperatorRefund, DateTime.UtcNow.AddDays(30), "R-ONTIME");
        AddLine(onTime.Id, supplierA.Id, 1000m, 0m);

        var dueSoon = await AddBcAsync(BookingCancellationStatus.AwaitingOperatorRefund, DateTime.UtcNow.AddDays(3), "R-DUESOON");
        AddLine(dueSoon.Id, supplierA.Id, 500m, 100m); // estimado = 400

        var overdue = await AddBcAsync(BookingCancellationStatus.AwaitingOperatorRefund, DateTime.UtcNow.AddDays(-2), "R-OVERDUE");
        AddLine(overdue.Id, supplierA.Id, 700m, 0m);

        var abandoned = await AddBcAsync(BookingCancellationStatus.AbandonedByOperator, DateTime.UtcNow.AddDays(-90), "R-ABANDONED");
        AddLine(abandoned.Id, supplierA.Id, 300m, 0m);

        // BC de OTRO operador (no debe aparecer en la consulta de A).
        var onlyB = await AddBcAsync(BookingCancellationStatus.AwaitingOperatorRefund, DateTime.UtcNow.AddDays(10), "R-ONLYB");
        // re-apunta SupplierId del BC a B para coherencia, pero el read-model agrupa por linea:
        AddLine(onlyB.Id, supplierB.Id, 200m, 0m);

        // BC Closed con RESIDUO vivo (cap 999, recibido 0 => el operador no devolvio nada): con la ampliacion
        // RESTOS (2026-07-03) esta fila SI aparece, rotulada "cerrada con resto", para que la solapa cuadre con el
        // "me tiene que devolver" del extracto.
        var closed = await AddBcAsync(BookingCancellationStatus.Closed, DateTime.UtcNow.AddDays(-1), "R-CLOSED");
        AddLine(closed.Id, supplierA.Id, 999m, 0m);

        await ctx.SaveChangesAsync();
        return (supplierA.Id, supplierB.Id);
    }

    [Fact]
    public async Task ReadModel_supplierScoped_filtersAndSemaphoresAndUnmaskedAmounts()
    {
        await using var ctx = NewDbContext();
        var (supplierAId, _) = await SeedReadModelAsync(ctx);

        var svc = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var items = await svc.GetSupplierPendingRefundsAsync(supplierAId, CancellationToken.None);

        // 4 cancelaciones activas de A (OnTime/DueSoon/Overdue/Abandoned) + la Closed con residuo = 5. El BC de B
        // se excluye (otro operador).
        Assert.Equal(5, items.Count);
        Assert.DoesNotContain(items, i => i.NumeroReserva == "R-ONLYB");

        // RESTOS: la Closed con residuo aparece como "cerrada con resto" y NO admite registro directo de reembolso.
        var closed = items.Single(i => i.NumeroReserva == "R-CLOSED");
        Assert.Equal(OperatorRefundRowStatus.ClosedWithResidue, closed.RowStatus);
        Assert.False(closed.CanRegisterRefund);
        Assert.Equal(999m, closed.EstimatedRefundsByCurrency.Single().EstimatedAmount);

        var dueSoon = items.Single(i => i.NumeroReserva == "R-DUESOON");
        Assert.Equal(OperatorRefundPendingSemaphore.DueSoon, dueSoon.Semaphore);
        Assert.False(dueSoon.AmountsMasked);
        Assert.Equal(400m, dueSoon.EstimatedRefundsByCurrency.Single().EstimatedAmount); // 500 - 100

        Assert.Equal(OperatorRefundPendingSemaphore.Abandoned,
            items.Single(i => i.NumeroReserva == "R-ABANDONED").Semaphore);
        Assert.Equal(OperatorRefundPendingSemaphore.Overdue,
            items.Single(i => i.NumeroReserva == "R-OVERDUE").Semaphore);
        Assert.Equal(OperatorRefundPendingSemaphore.OnTime,
            items.Single(i => i.NumeroReserva == "R-ONTIME").Semaphore);
    }

    [Fact]
    public async Task ReadModel_canReopenForLateRefund_trueForAbandonedAndClosedWithResidue()
    {
        await using var ctx = NewDbContext();
        var (supplierAId, _) = await SeedReadModelAsync(ctx);

        var svc = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var items = await svc.GetSupplierPendingRefundsAsync(supplierAId, CancellationToken.None);

        // Abandonada -> reabrible.
        Assert.True(items.Single(i => i.NumeroReserva == "R-ABANDONED").CanReopenForLateRefund);
        // Cerrada CON residuo (cap 999, recibido 0) -> reabrible por reembolso tardio (FIX A).
        Assert.True(items.Single(i => i.NumeroReserva == "R-CLOSED").CanReopenForLateRefund);
        // Esperando (activa, no terminal) -> NO se reabre (ya esta abierta, se registra directo).
        Assert.False(items.Single(i => i.NumeroReserva == "R-ONTIME").CanReopenForLateRefund);
        Assert.True(items.Single(i => i.NumeroReserva == "R-ONTIME").CanRegisterRefund);
    }

    [Fact]
    public async Task ReadModel_withoutSeeCost_masksAmounts()
    {
        await using var ctx = NewDbContext();
        var (supplierAId, _) = await SeedReadModelAsync(ctx);

        // Sin httpContextAccessor -> CostMasking fail-closed -> montos enmascarados.
        var svc = new OperatorRefundReadModelService(ctx, httpContextAccessor: null, permissionResolver: null);
        var items = await svc.GetSupplierPendingRefundsAsync(supplierAId, CancellationToken.None);

        Assert.All(items, i =>
        {
            Assert.True(i.AmountsMasked);
            Assert.All(i.EstimatedRefundsByCurrency, e => Assert.Equal(0m, e.EstimatedAmount));
        });
    }

    [Fact]
    public async Task ReadModel_global_includesAllSuppliers()
    {
        await using var ctx = NewDbContext();
        await SeedReadModelAsync(ctx);

        var svc = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var items = await svc.GetAllPendingRefundsAsync(CancellationToken.None);

        // RESTOS: 4 activas de A + 1 de B + la Closed con residuo de A = 6.
        Assert.Equal(6, items.Count);
        Assert.Contains(items, i => i.NumeroReserva == "R-ONLYB");
        Assert.Contains(items, i => i.NumeroReserva == "R-CLOSED");
    }

    // =====================================================================================
    // MENOR 1: simetria del void sobre las lineas del operador (funcion pura)
    // =====================================================================================

    [Fact]
    public void RemoveReceivedRefundFromOperatorLines_isInverseOfDistribute()
    {
        var lines = new List<BookingCancellationLine>
        {
            new() { SupplierId = 1, RefundCap = 300m, ReceivedRefundAmount = 0m, Currency = "ARS" },
            new() { SupplierId = 1, RefundCap = 200m, ReceivedRefundAmount = 0m, Currency = "ARS" },
        };

        OperatorRefundService.DistributeReceivedRefundToOperatorLines(lines, 500m);
        Assert.Equal(500m, lines.Sum(l => l.ReceivedRefundAmount)); // se imputo todo

        // Anular esa misma allocation devuelve las lineas a cero: el read-model no subestima despues del void.
        OperatorRefundService.RemoveReceivedRefundFromOperatorLines(lines, 500m);
        Assert.Equal(0m, lines.Sum(l => l.ReceivedRefundAmount));
        Assert.All(lines, l => Assert.Equal(BookingCancellationLineRefundStatus.None, l.RefundStatus));
    }

    [Fact]
    public void RemoveReceivedRefundFromOperatorLines_partialVoid_dropsBelowCapAndReopens()
    {
        var lines = new List<BookingCancellationLine>
        {
            new()
            {
                SupplierId = 1, RefundCap = 300m, ReceivedRefundAmount = 300m,
                RefundStatus = BookingCancellationLineRefundStatus.Settled, Currency = "ARS",
            },
        };

        OperatorRefundService.RemoveReceivedRefundFromOperatorLines(lines, 100m);

        Assert.Equal(200m, lines[0].ReceivedRefundAmount);
        // Bajo del cap -> vuelve a "pendiente del operador" (ya no Settled).
        Assert.Equal(BookingCancellationLineRefundStatus.PendingOperatorRefund, lines[0].RefundStatus);
    }
}
