using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-07-04, DECISION DEL DUEÑO) Con el auto-cierre, una anulacion sin plata al operador se cierra de una
/// (BC -> Closed, reserva -> Cancelled) AUNQUE la multa siga sin decidir. La pregunta de la multa queda como una
/// TAREA PENDIENTE que se responde DESPUES del cierre. Esta suite demuestra que la pata de la multa sigue 100%
/// operable con la BC en <c>Closed</c>:
/// <list type="bullet">
///   <item>el read-model sigue diciendo "Pending" (el cartel "falta decidir la multa" se muestra);</item>
///   <item>cerrar sin multa (waive) funciona desde Closed y es no-op de estado (ya estaba cerrada);</item>
///   <item>confirmar la multa funciona desde Closed y emite/vincula la Nota de Debito, sin explotar por estar
///   Closed (la emision de la ND NO transiciona el estado de la BC).</item>
/// </list>
///
/// <para>InMemory de EF + mocks, sin Docker (mismo trade-off que el resto de la suite de cancelacion).</para>
/// </summary>
public class BookingCancellationPenaltyOperableFromClosedTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bc-penalty-operable-from-closed-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (BookingCancellationService Service, Mock<IInvoiceService> InvoiceMock) BuildService(AppDbContext ctx)
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
                EnableCancellationDebitNote = true,
                CancellationDebitNoteFourEyesThreshold = 2_000_000m,
                OperatorRefundTimeoutDays = 60,
            });

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalMock.Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            calculatorMock.Object, adminCountMock.Object);

        return (service, invoiceMock);
    }

    /// <summary>
    /// Escenario: la anulacion ya se cerro sin plata al operador (BC en <c>Closed</c>, reserva <c>Cancelled</c>),
    /// pero la multa NUNCA se decidio (PenaltyStatus <c>Estimated</c>). NC total con CAE ya emitida. Una linea con
    /// RefundCap 0 (nunca hubo circuito de reembolso). Factura C=11 en ARS apta para emitir la ND.
    /// </summary>
    private static async Task<(Guid BcPublicId, BookingCancellation Bc, Invoice Original, Reserva Reserva)> SeedClosedWithUndecidedPenaltyAsync(
        AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-CLOSED-PENALTY",
            Name = "R-CLOSED-PENALTY",
            PayerId = customer.Id,
            Status = EstadoReserva.Cancelled, // ya cerrada por el auto-cierre
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 100,
            CAE = "12345678", Resultado = "A", MonId = "PES",
            ImporteTotal = 100_000m, ImporteNeto = 100_000m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 101,
            CAE = "99999999", Resultado = "A", ReservaId = reserva.Id, OriginalInvoiceId = original.Id,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id,
            CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.Closed, // ya cerrada
            ClosedAt = DateTime.UtcNow.AddMinutes(-5),
            PenaltyStatus = PenaltyStatus.Estimated, // multa aun SIN decidir
            Reason = "Anulacion cerrada sin plata al operador; multa pendiente de decidir",
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
            LineSaleAmount = 0m,
            RefundCap = 0m,
            ReceivedRefundAmount = 0m,
        });
        await ctx.SaveChangesAsync();

        return (bc.PublicId, bc, original, reserva);
    }

    [Fact]
    public async Task Cerrada_ConMultaSinDecidir_OutcomeEsPending()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        var (_, _, _, reserva) = await SeedClosedWithUndecidedPenaltyAsync(ctx);

        // Aunque la reserva ya este Cancelled, el paso de la multa sigue pendiente (el cartel se muestra).
        Assert.Equal(
            OperatorPenaltyOutcome.Pending,
            await service.GetOperatorPenaltyOutcomeAsync(reserva.PublicId, CancellationToken.None));
    }

    [Fact]
    public async Task Cerrada_WaiveDesdeClosed_Funciona_YSigueCerrada()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx);
        var (bcId, bc, _, reserva) = await SeedClosedWithUndecidedPenaltyAsync(ctx);

        await service.WaiveOperatorPenaltyAsync(
            bcId, "El operador confirmo que no cobra multa.", "u", "U", CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Waived, bcAfter.PenaltyStatus);
        // Sigue Closed (el auto-cierre post-waive es no-op: ya estaba cerrada).
        Assert.Equal(BookingCancellationStatus.Closed, bcAfter.Status);

        var reservaAfter = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, reservaAfter.Status);

        // Y ya no figura pendiente.
        Assert.Equal(
            OperatorPenaltyOutcome.Waived,
            await service.GetOperatorPenaltyOutcomeAsync(reserva.PublicId, CancellationToken.None));
    }

    [Fact]
    public async Task Cerrada_ConfirmarMultaDesdeClosed_EmiteLaNd_SinExplotar()
    {
        await using var ctx = NewDbContext();
        var (service, invoiceMock) = BuildService(ctx);
        var (bcId, bc, original, reserva) = await SeedClosedWithUndecidedPenaltyAsync(ctx);

        // CreateAsync emite una ND en la BD InMemory y devuelve su DTO (como en el flujo real de confirmar).
        invoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                var nd = new Invoice
                {
                    TipoComprobante = 12, // ND C
                    PuntoDeVenta = 1,
                    NumeroComprobante = 200,
                    Resultado = "PENDING",
                    ReservaId = reserva.Id,
                    OriginalInvoiceId = original.Id,
                };
                ctx.Invoices.Add(nd);
                ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });

        // Confirmar la multa DESDE Closed: el gate exige estado post-NC con CAE, y Closed esta en ese set.
        await service.ConfirmPenaltyAsync(
            bcId,
            new ConfirmPenaltyRequest(
                ConceptKind: CancellationConceptKind.AgencyManagementFee,
                ConfirmedPenaltyAmount: 30_000m,
                OperatorConfirmationDate: DateTime.UtcNow.AddDays(-2),
                SupportingDocumentReference: "https://docs/mail.pdf"),
            "u", "U", requesterIsAdmin: false, CancellationToken.None, userCanClassifyAgencyPenalty: true);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        // Multa confirmada + ND vinculada; la BC sigue Closed (la emision de la ND NO cambia el estado de la BC).
        Assert.Equal(PenaltyStatus.Confirmed, bcAfter.PenaltyStatus);
        Assert.Equal(DebitNoteStatus.Pending, bcAfter.DebitNoteStatus);
        Assert.NotNull(bcAfter.DebitNoteInvoiceId);
        Assert.Equal(BookingCancellationStatus.Closed, bcAfter.Status);

        var reservaAfter = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, reservaAfter.Status);

        Assert.Equal(
            OperatorPenaltyOutcome.Confirmed,
            await service.GetOperatorPenaltyOutcomeAsync(reserva.PublicId, CancellationToken.None));
    }
}
