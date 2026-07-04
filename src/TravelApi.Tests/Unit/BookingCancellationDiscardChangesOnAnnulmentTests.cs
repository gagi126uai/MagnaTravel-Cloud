using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-07-03, ADR-027) Al ANULAR una reserva se descarta la marca "confirmada con cambios" que hubiera quedado
/// pendiente de revisar. Antes, esa marca (<c>HasUnacknowledgedChanges</c> + <c>ChangesPendingSince</c> + las filas
/// de detalle <c>ReservaPendingChanges</c>) solo se limpiaba al dar el OK humano; el flujo de anulacion no la tocaba,
/// asi que una reserva anulada seguia mostrando el cartel "Se editaron precios..." y el badge "Con cambios".
///
/// <para>Verificamos que la limpieza corre en dos transiciones representativas: (a) la transicion post-CAE (callback
/// de ARCA) que lleva la reserva a <c>PendingOperatorRefund</c>, y (b) el auto-cierre sin reembolso que la lleva a
/// <c>Cancelled</c>. Y que NO registra un OK humano (<c>ChangesAckBy*</c> quedan null): la anulacion DESCARTA los
/// cambios, no los "revisa". El flujo normal del OK (<c>AcknowledgeChangesAsync</c>) sigue cubierto por
/// <c>Adr027ConfirmedWithChangesTests</c> (no se toca aca).</para>
///
/// <para>InMemory de EF (mismo trade-off que el resto de la suite de cancelacion): alcanza para la logica de la
/// transicion + la limpieza de la marca y sus filas hijas.</para>
/// </summary>
public class BookingCancellationDiscardChangesOnAnnulmentTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bc-discard-changes-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildService(AppDbContext ctx)
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
                EnableCancellationDebitNote = false, // sin gestion de multa: no bloquea el auto-cierre
                OperatorRefundTimeoutDays = 60,
            });

        return new BookingCancellationService(
            ctx, invoiceMock.Object, approvalMock.Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            calculatorMock.Object, adminCountMock.Object);
    }

    /// <summary>
    /// Arma reserva + factura + BC + lineas + la marca "confirmada con cambios" (flag + fecha + 1 fila de detalle).
    /// La reserva nace en <paramref name="reservaStatus"/> y el BC en <paramref name="bcStatus"/>. Cada linea es
    /// (RefundCap, Received): RefundCap 0 = el operador no debe devolver nada (receivable $0 -> gatilla auto-cierre).
    /// </summary>
    private static async Task<(BookingCancellation Bc, Invoice OriginatingInvoice, Reserva Reserva)> SeedMarkedAsync(
        AppDbContext ctx,
        IReadOnlyList<(decimal RefundCap, decimal Received)> lines,
        BookingCancellationStatus bcStatus,
        string reservaStatus)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-MARK",
            Name = "R-MARK",
            PayerId = customer.Id,
            Status = reservaStatus,
            // La marca "confirmada con cambios" colgada (lo que esta anulacion debe descartar).
            HasUnacknowledgedChanges = true,
            ChangesPendingSince = DateTime.UtcNow.AddDays(-3),
        };
        var invoice = new Invoice { TipoComprobante = 1, Resultado = "A", CAE = "12345678901234" };
        ctx.Reservas.Add(reserva);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        // Detalle de "qué cambió" (una fila): debe borrarse al anular.
        ctx.ReservaPendingChanges.Add(new ReservaPendingChange
        {
            ReservaId = reserva.Id,
            ServiceType = "Hotel",
            ServiceDescription = "Hotel 4 estrellas",
            Field = "SalePrice",
            OldValue = 100_000m,
            NewValue = 120_000m,
            Currency = "ARS",
            ChangedAt = DateTime.UtcNow.AddDays(-3),
        });

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            CreditNoteInvoiceId = 999,
            Status = bcStatus,
            PenaltyStatus = PenaltyStatus.Confirmed,
            DebitNoteStatus = DebitNoteStatus.NotApplicable,
            Reason = "Anulacion de prueba",
            DraftedByUserId = "vendedor-1",
            DraftedByUserName = "Juan Vendedor",
            OperatorRefundDueBy = DateTime.UtcNow.AddDays(30),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        foreach (var (refundCap, received) in lines)
        {
            ctx.BookingCancellationLines.Add(new BookingCancellationLine
            {
                BookingCancellationId = bc.Id,
                SupplierId = supplier.Id,
                ServiceTable = CancellableServiceTable.Hotel,
                ServiceId = 1,
                Scope = BookingCancellationLineScope.Full,
                Currency = "USD",
                LineSaleAmount = refundCap,
                RefundCap = refundCap,
                ReceivedRefundAmount = received,
            });
        }
        await ctx.SaveChangesAsync();

        return (bc, invoice, reserva);
    }

    [Fact]
    public async Task TransicionPostCae_ALaEsperaDeReembolso_DescartaLaMarcaYBorraElDetalle()
    {
        // Reserva VIVA (Confirmed) con la marca colgada. Con receivable > 0 la anulacion NO se cierra sola: pasa a
        // PendingOperatorRefund. En esa transicion (post-CAE) se descarta la marca "confirmada con cambios".
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, invoice, reserva) = await SeedMarkedAsync(ctx,
            lines: new[] { (RefundCap: 400m, Received: 0m) },
            bcStatus: BookingCancellationStatus.AwaitingFiscalConfirmation,
            reservaStatus: EstadoReserva.Confirmed);

        await service.OnArcaSucceededAsync(invoice.Id, creditNoteInvoiceId: 555, CancellationToken.None);

        var after = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, after.Status);
        // Marca descartada.
        Assert.False(after.HasUnacknowledgedChanges);
        Assert.Null(after.ChangesPendingSince);
        // NO es un OK humano: no se registra quien acuso.
        Assert.Null(after.ChangesAckByUserId);
        Assert.Null(after.ChangesAckByUserName);
        Assert.Null(after.ChangesAckAt);
        // El detalle de "qué cambió" se borro.
        var detail = await ctx.ReservaPendingChanges.AsNoTracking()
            .CountAsync(c => c.ReservaId == reserva.Id);
        Assert.Equal(0, detail);
    }

    [Fact]
    public async Task AutoCierreSinReembolso_ACancelada_DescartaLaMarcaYBorraElDetalle()
    {
        // Reserva ya en PendingOperatorRefund (como la deja el confirm en prod) con la marca aun colgada (data
        // legacy: antes de este fix la marca no se limpiaba al anular). El auto-cierre por receivable $0 la lleva a
        // Cancelled y debe descartar la marca en esa transicion.
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, invoice, reserva) = await SeedMarkedAsync(ctx,
            lines: new[] { (RefundCap: 0m, Received: 0m) },
            bcStatus: BookingCancellationStatus.AwaitingFiscalConfirmation,
            reservaStatus: EstadoReserva.PendingOperatorRefund);

        await service.OnArcaSucceededAsync(invoice.Id, creditNoteInvoiceId: 555, CancellationToken.None);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.Closed, bcAfter.Status);

        var after = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, after.Status);
        Assert.False(after.HasUnacknowledgedChanges);
        Assert.Null(after.ChangesPendingSince);
        Assert.Null(after.ChangesAckByUserId);
        var detail = await ctx.ReservaPendingChanges.AsNoTracking()
            .CountAsync(c => c.ReservaId == reserva.Id);
        Assert.Equal(0, detail);
    }

    [Fact]
    public async Task Anulacion_SinMarcaPendiente_NoRompe_YNoInventaAck()
    {
        // Reserva SIN marca (caso mayoritario): la anulacion corre igual y no escribe basura de acuse.
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, invoice, reserva) = await SeedMarkedAsync(ctx,
            lines: new[] { (RefundCap: 400m, Received: 0m) },
            bcStatus: BookingCancellationStatus.AwaitingFiscalConfirmation,
            reservaStatus: EstadoReserva.Confirmed);

        // Quitamos la marca sembrada para simular una reserva "limpia".
        var tracked = await ctx.Reservas.FirstAsync(r => r.Id == reserva.Id);
        tracked.HasUnacknowledgedChanges = false;
        tracked.ChangesPendingSince = null;
        ctx.ReservaPendingChanges.RemoveRange(ctx.ReservaPendingChanges.Where(c => c.ReservaId == reserva.Id));
        await ctx.SaveChangesAsync();

        await service.OnArcaSucceededAsync(invoice.Id, creditNoteInvoiceId: 555, CancellationToken.None);

        var after = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, after.Status);
        Assert.False(after.HasUnacknowledgedChanges);
        Assert.Null(after.ChangesAckByUserId);
    }
}
