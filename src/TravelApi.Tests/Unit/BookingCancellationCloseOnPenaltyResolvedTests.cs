using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-07-03) Cierre INMEDIATO al resolver la pata de la multa del operador. El auto-cierre de anulaciones sin
/// reembolso pendiente esta BLOQUEADO mientras la multa sigue sin decidir; por eso, apenas la multa se resuelve
/// (cerrar sin multa, o su Nota de Debito queda emitida), hay que re-evaluar el cierre EN EL MOMENTO — sino la
/// reserva se queda "esperando reembolso" hasta el barrido nocturno de las 4am.
///
/// <para>Cubrimos: (a) cerrar sin multa (waive) sin circuito de reembolso -> cierra ya; (b) waive CON plata pagada
/// al operador (RefundCap &gt; 0) -> NO cierra (sigue esperando el reembolso); (c) la ND re-vinculada que queda
/// Issued (reintento) -> cierra ya; (d) doble waive sigue rebotando (no se rompe la idempotencia existente).</para>
///
/// <para>InMemory de EF + mocks, sin Docker (mismo trade-off que el resto de la suite de cancelacion).</para>
/// </summary>
public class BookingCancellationCloseOnPenaltyResolvedTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bc-close-on-penalty-resolved-{Guid.NewGuid()}")
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
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnableCancellationDebitNote = true, // la gestion de la multa DEBE estar disponible para waive/retry
                OperatorRefundTimeoutDays = 60,
            });

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalMock.Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            calculatorMock.Object, adminCountMock.Object);

        return (service, auditMock);
    }

    /// <summary>
    /// Reserva en PendingOperatorRefund + factura de venta (con CAE) + NC total (con CAE) + BC post-NC
    /// (AwaitingOperatorRefund) + UNA linea con (RefundCap, Received). Con RefundCap 0 no hay circuito de reembolso
    /// con el operador (receivable $0). <paramref name="penaltyStatus"/> arranca en Estimated (multa sin resolver).
    /// </summary>
    private static async Task<(Guid BcPublicId, BookingCancellation Bc, Invoice Original, Reserva Reserva)> SeedAsync(
        AppDbContext ctx,
        decimal refundCap,
        PenaltyStatus penaltyStatus = PenaltyStatus.Estimated,
        int? debitNoteInvoiceId = null,
        DebitNoteStatus debitNoteStatus = DebitNoteStatus.NotApplicable)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-RESOLVE",
            Name = "R-RESOLVE",
            PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund,
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 100,
            CAE = "12345678", Resultado = "A", ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 101,
            CAE = "99999999", Resultado = "A", ReservaId = reserva.Id,
        };
        ctx.Invoices.Add(original);
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();
        creditNote.OriginalInvoiceId = original.Id;
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id,
            CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyStatus = penaltyStatus,
            DebitNoteInvoiceId = debitNoteInvoiceId,
            DebitNoteStatus = debitNoteStatus,
            Reason = "Anulacion; resolviendo la pata de la multa",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-5),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Full,
            Currency = "ARS",
            LineSaleAmount = refundCap,
            RefundCap = refundCap,
            ReceivedRefundAmount = 0m,
        });
        await ctx.SaveChangesAsync();

        return (bc.PublicId, bc, original, reserva);
    }

    // ============================ (a) waive sin circuito de reembolso -> cierra ya ============================

    [Fact]
    public async Task Waive_SinCircuitoDeReembolso_CierraEnElMomento()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);
        var (bcId, bc, _, reserva) = await SeedAsync(ctx, refundCap: 0m);

        await service.WaiveOperatorPenaltyAsync(
            bcId, "El operador confirmo que no cobra multa.", "u", "U", CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Waived, bcAfter.PenaltyStatus);
        // Cierre INMEDIATO (no espera al barrido).
        Assert.Equal(BookingCancellationStatus.Closed, bcAfter.Status);
        Assert.NotNull(bcAfter.ClosedAt);

        var reservaAfter = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, reservaAfter.Status);

        // Audit del cierre con el origin distinguible "resolucion-multa".
        auditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.BookingCancellationClosedNoOperatorRefundDue,
            AuditActions.BookingCancellationEntityName,
            bc.Id.ToString(),
            It.Is<string>(details => details.Contains("resolucion-multa")),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    // ============================ (b) waive con reembolso pendiente -> NO cierra ============================

    [Fact]
    public async Task Waive_ConPlataPagadaAlOperador_NoCierra_SigueEsperandoReembolso()
    {
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx);
        // RefundCap > 0: el operador tiene que devolver la plata pagada -> hay circuito de reembolso vivo.
        var (bcId, bc, _, reserva) = await SeedAsync(ctx, refundCap: 100_000m);

        await service.WaiveOperatorPenaltyAsync(
            bcId, "El operador no cobra multa pero SI devuelve lo pagado.", "u", "U", CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Waived, bcAfter.PenaltyStatus);
        // NO se cierra: sigue esperando el reembolso del operador.
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcAfter.Status);
        Assert.Null(bcAfter.ClosedAt);

        var reservaAfter = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reservaAfter.Status);

        auditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.BookingCancellationClosedNoOperatorRefundDue,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // ============================ (c) ND que queda Issued (reintento) -> cierra ya ============================

    [Fact]
    public async Task RetryDebitNote_ReVinculaNdEmitida_SinReembolso_CierraEnElMomento()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        // Multa ya confirmada, ND sin vincular al BC (quedo a medias), sin circuito de reembolso (RefundCap 0).
        var (bcId, bc, original, reserva) = await SeedAsync(
            ctx, refundCap: 0m, penaltyStatus: PenaltyStatus.Confirmed);

        // ND huerfana ya EMITIDA (Resultado A + CAE) para la misma factura original y reserva: el reintento la
        // re-vincula y queda Issued -> la pata de la multa queda resuelta.
        var orphanNd = new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = 300,
            Resultado = "A",
            CAE = "55555555",
            ReservaId = reserva.Id,
            OriginalInvoiceId = original.Id,
        };
        ctx.Invoices.Add(orphanNd);
        await ctx.SaveChangesAsync();

        await service.RetryDebitNoteEmissionAsync(
            bcId, "u", "U", CancellationToken.None, userCanClassifyAgencyPenalty: true);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(DebitNoteStatus.Issued, bcAfter.DebitNoteStatus);
        Assert.Equal(orphanNd.Id, bcAfter.DebitNoteInvoiceId);
        // Cierre INMEDIATO tras resolverse la ND.
        Assert.Equal(BookingCancellationStatus.Closed, bcAfter.Status);
        Assert.NotNull(bcAfter.ClosedAt);

        var reservaAfter = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, reservaAfter.Status);
    }

    // ============================ (d) doble waive sigue rebotando ============================

    [Fact]
    public async Task Waive_Dos_Veces_Segunda_Rebota_409()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        var (bcId, _, _, _) = await SeedAsync(ctx, refundCap: 0m);

        await service.WaiveOperatorPenaltyAsync(
            bcId, "Sin multa.", "u", "U", CancellationToken.None, userCanClassifyAgencyPenalty: true);

        // El primer waive cerro la anulacion; el segundo debe rebotar 409 por idempotencia (no reabrir un cerrado).
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.WaiveOperatorPenaltyAsync(
                bcId, "Sin multa otra vez.", "u", "U", CancellationToken.None, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-WAIVE-003", ex.InvariantCode);
    }
}
