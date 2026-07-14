using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Application.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-014 (Fase 1, 2026-06-02) — tests UNIT de la confirmacion DIFERIDA de la penalidad
/// (<c>ConfirmPenaltyAsync</c>) + la extension de la bandeja para la ND huerfana.
///
/// <para>Mismo enfoque que <see cref="BookingCancellationServicePartialCreditNoteTests"/>:
/// DbContext InMemory + mocks de las 8 deps, sin Docker. InMemory NO valida CHECK
/// constraints SQL ni xmin (esos casos van a integracion VPS, ver §6 del ADR). Lo que SI
/// cubrimos aca: precondiciones del endpoint, exactly-once via la marca Confirmed (pre-check),
/// B2 (balance neutro), 4-eyes, anti-doble-cobro diferido (ruteo a manual), y la
/// re-vinculacion de ND huerfana en la bandeja.</para>
/// </summary>
public class CancellationDeferredPenaltyTests
{
    // ============================================================
    // Builders
    // ============================================================

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr014-deferred-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private sealed record Harness(
        BookingCancellationService Service,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceMock,
        Mock<IAuditService> AuditMock,
        OperationalFinanceSettings Settings,
        CapturingLogger<BookingCancellationService> Log);

    /// <summary>
    /// Logger de test que guarda en memoria los mensajes Warning para poder asertar la rama
    /// de plazo vencido (M4), que es solo logging y no muta estado. Captura el mensaje YA
    /// formateado (con los valores de los placeholders) usando el formatter de ILogger.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static Harness BuildService(bool flagOn = true)
    {
        var ctx = NewDbContext();
        var invoiceMock = new Mock<IInvoiceService>();
        var approvalMock = new Mock<IApprovalRequestService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        var adminCountMock = new Mock<IAdminUserCountService>();

        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnableCancellationDebitNote = flagOn,
            CancellationDebitNoteGraceDays = 15,
            CancellationDebitNoteHardWarnDays = 60,
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
        };
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var log = new CapturingLogger<BookingCancellationService>();

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            approvalMock.Object,
            auditMock.Object,
            log,
            settingsMock.Object,
            calculatorMock.Object,
            adminCountMock.Object);

        return new Harness(service, ctx, invoiceMock, auditMock, settings, log);
    }

    /// <summary>
    /// Semilla del caso post-NC: factura C=11 con CAE, reserva, supplier (PenaltyOwnership por
    /// parametro), BC en AwaitingOperatorRefund con CreditNoteInvoiceId seteado (NC con CAE),
    /// concepto pass-through + Estimated (estado tipico al llegar al Dia N).
    /// </summary>
    private static async Task<(Guid BcPublicId, BookingCancellation Bc, Invoice Original)> SeedPostNcAsync(
        AppDbContext ctx,
        PenaltyOwnership ownership = PenaltyOwnership.Agency,
        int tipoComprobante = 11,
        BookingCancellationStatus status = BookingCancellationStatus.AwaitingOperatorRefund,
        string? reservaStatus = null,
        decimal originalTotal = 100_000m)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador X", IsActive = true, PenaltyOwnership = ownership };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-014",
            Name = "Reserva Test",
            PayerId = customer.Id,
            Status = reservaStatus ?? EstadoReserva.PendingOperatorRefund,
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = 100,
            CAE = "12345678",
            Resultado = "A",
            MonId = "PES",
            ImporteTotal = originalTotal,
            ImporteNeto = originalTotal,
            ImporteIva = 0m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        var creditNote = new Invoice
        {
            TipoComprobante = 13, // NC C
            PuntoDeVenta = 1,
            NumeroComprobante = 101,
            CAE = "99999999",
            Resultado = "A",
            ReservaId = reserva.Id,
            OriginalInvoiceId = null,
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
            Status = status,
            Reason = "Cliente cancelo; penalidad a confirmar por el operador",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-10),
            // defaults conservadores: pass-through / Estimated / NotApplicable
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc.PublicId, bc, original);
    }

    private static ConfirmPenaltyRequest Request(
        decimal amount = 30_000m,
        DateTime? date = null,
        string? support = "https://docs/operador-mail.pdf",
        CancellationConceptKind? concept = CancellationConceptKind.AgencyManagementFee)
        => new ConfirmPenaltyRequest(
            ConceptKind: concept,
            ConfirmedPenaltyAmount: amount,
            OperatorConfirmationDate: date ?? DateTime.UtcNow.AddDays(-2),
            DebitNotePurpose: null,
            SupportingDocumentReference: support);

    /// <summary>Hace que CreateAsync inserte una ND en la BD InMemory y devuelva su DTO.</summary>
    private static void SetupCreateEmitsDebitNote(Harness h)
    {
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                var reservaId = h.Ctx.Reservas.First().Id;
                var originalId = h.Ctx.Invoices.First(i => i.TipoComprobante == 11).Id;
                var nd = new Invoice
                {
                    TipoComprobante = 12, // ND C
                    PuntoDeVenta = 1,
                    NumeroComprobante = 200,
                    Resultado = "PENDING",
                    ReservaId = reservaId,
                    OriginalInvoiceId = originalId,
                };
                h.Ctx.Invoices.Add(nd);
                h.Ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });
    }

    // ============================================================
    // Precondiciones
    // ============================================================

    [Fact]
    public async Task FlagOff_RejectsAndDoesNotMutate()
    {
        var h = BuildService(flagOn: false);
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));

        // Byte-identidad: nada muto.
        var bc = h.Ctx.BookingCancellations.Single();
        Assert.Equal(PenaltyStatus.Estimated, bc.PenaltyStatus);
        Assert.Null(bc.OperatorPenaltyConfirmedDate);
        Assert.Equal(DebitNoteStatus.NotApplicable, bc.DebitNoteStatus);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BcNotFound_Throws404()
    {
        var h = BuildService();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            h.Service.ConfirmPenaltyAsync(Guid.NewGuid(), Request(), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));
    }

    [Fact]
    public async Task WithoutPermission_ThrowsPermInvariant()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(), "vendedor", "V", false, default,
                userCanClassifyAgencyPenalty: false));
        Assert.Equal("INV-ADR014-PERM", ex.InvariantCode);
    }

    [Fact]
    public async Task StateNotPostCreditNote_RejectsInv001()
    {
        var h = BuildService();
        // Drafted = la NC todavia no salio.
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx, status: BookingCancellationStatus.Drafted);
        // Quitamos el CreditNoteInvoiceId para reflejar "NC sin CAE".
        var bc = h.Ctx.BookingCancellations.Single();
        bc.CreditNoteInvoiceId = null;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-001", ex.InvariantCode);
    }

    [Fact]
    public async Task PassThroughConcept_NowEmits_SignedFiscalRule()
    {
        // REGLA FISCAL CERRADA (firmada): la penalidad pass-through del operador SI emite ND al
        // cliente. Antes el confirm-penalty rechazaba pass-through con INV-ADR014-002 ("no es ingreso
        // propio"); ahora, con el resto del gating cumplido (factura C, ARS, monto valido), EMITE.
        var h = BuildService();
        SetupCreateEmitsDebitNote(h);
        // Operador retiene la penalidad -> default pass-through; pedimos sin concepto explicito.
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx, ownership: PenaltyOwnership.Operator);

        var dto = await h.Service.ConfirmPenaltyAsync(bcId, Request(concept: null), "u", "U", false, default,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.NotNull(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Pending, reloaded.DebitNoteStatus);
        Assert.Equal(CancellationConceptKind.OperatorPenaltyPassThrough, reloaded.ConceptKind);
        Assert.Equal("Pending", dto.DebitNoteStatus);
    }

    [Fact]
    public async Task InsuranceConcept_StillRejectsInv002()
    {
        // Solo los conceptos que NO emiten ND (seguros) siguen rechazando con INV-ADR014-002.
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx, ownership: PenaltyOwnership.Agency);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bcId, Request(concept: CancellationConceptKind.RealInsurancePremium), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-002", ex.InvariantCode);
    }

    [Fact]
    public async Task FutureDate_Throws400()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(date: DateTime.UtcNow.AddDays(2)),
                "u", "U", false, default, userCanClassifyAgencyPenalty: true));
    }

    [Fact]
    public async Task DateBeforeCancellation_Throws400()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);
        // ConfirmedWithClientAt = hoy-10; mandamos hoy-30 (anterior).
        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(date: DateTime.UtcNow.AddDays(-30)),
                "u", "U", false, default, userCanClassifyAgencyPenalty: true));
    }

    // ============================================================
    // Exactly-once / idempotencia (B1)
    // ============================================================

    [Fact]
    public async Task AlreadyConfirmed_RejectsInv003_NoSecondDebitNote()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);
        // Simular una corrida previa: penalidad ya confirmada.
        var bc = h.Ctx.BookingCancellations.Single();
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-003", ex.InvariantCode);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DebitNoteAlreadyIssued_RejectsInv003()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);
        var bc = h.Ctx.BookingCancellations.Single();
        bc.DebitNoteStatus = DebitNoteStatus.Issued;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-003", ex.InvariantCode);
    }

    [Fact]
    public async Task HappyPath_ConfirmsMarkBeforeEmit_AndEnqueuesDebitNote()
    {
        var h = BuildService();
        var (bcId, _, original) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);

        var dto = await h.Service.ConfirmPenaltyAsync(bcId, Request(amount: 30_000m),
            "backoffice", "Back Office", false, default, userCanClassifyAgencyPenalty: true);

        var bc = h.Ctx.BookingCancellations.Single();
        Assert.Equal(PenaltyStatus.Confirmed, bc.PenaltyStatus);
        Assert.Equal(CancellationConceptKind.AgencyManagementFee, bc.ConceptKind);
        Assert.Equal(30_000m, bc.PenaltyAmountAtEvent);
        Assert.NotNull(bc.OperatorPenaltyConfirmedDate);
        Assert.Equal("backoffice", bc.PenaltyConfirmedByUserId);
        // La ND se encolo y vinculo (DebitNoteStatus=Pending, DebitNoteInvoiceId seteado).
        Assert.Equal(DebitNoteStatus.Pending, bc.DebitNoteStatus);
        Assert.NotNull(bc.DebitNoteInvoiceId);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryAfterHappyPath_RejectsInv003_OnlyOneDebitNote()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);

        await h.Service.ConfirmPenaltyAsync(bcId, Request(), "u", "U", false, default,
            userCanClassifyAgencyPenalty: true);

        // Reintento: rebota por Confirmed/ND-en-juego, no emite una segunda ND.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-003", ex.InvariantCode);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ============================================================
    // B2 — balance neutro
    // ============================================================

    [Fact]
    public async Task DebitNoteOnClosedReserva_DoesNotTouchBalanceOrStatus()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx,
            status: BookingCancellationStatus.Closed,
            reservaStatus: EstadoReserva.Cancelled);
        // EstadoReserva es una clase estatica de constantes string, no un enum.
        SetupCreateEmitsDebitNote(h);

        var reservaBefore = h.Ctx.Reservas.Single();
        var statusBefore = reservaBefore.Status;
        var balanceBefore = reservaBefore.Balance;

        await h.Service.ConfirmPenaltyAsync(bcId, Request(), "u", "U", false, default,
            userCanClassifyAgencyPenalty: true);

        var reservaAfter = h.Ctx.Reservas.Single();
        Assert.Equal(statusBefore, reservaAfter.Status);
        Assert.Equal(balanceBefore, reservaAfter.Balance);
        // El BC sigue Closed (no se reabre).
        Assert.Equal(BookingCancellationStatus.Closed, h.Ctx.BookingCancellations.Single().Status);
    }

    // ============================================================
    // 4-eyes (M2)
    // ============================================================

    [Fact]
    public async Task NoSupportingDocument_RequiresFourEyes()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);

        // Sin soporte documental y sin approval -> ApprovalRequiredException.
        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(support: null), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));
    }

    [Fact]
    public async Task AmountAboveThreshold_RequiresFourEyes_EvenWithSupport()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx, originalTotal: 5_000_000m);

        // Monto > 2.000.000 (umbral) aunque haya soporte -> 4-eyes.
        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(amount: 3_000_000m), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));
    }

    [Fact]
    public async Task LowAmountWithSupport_NoFourEyes_Emits()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);

        // Monto bajo + soporte documental -> no exige 4-eyes, emite directo.
        var dto = await h.Service.ConfirmPenaltyAsync(bcId, Request(amount: 30_000m),
            "u", "U", false, default, userCanClassifyAgencyPenalty: true);
        Assert.Equal(DebitNoteStatus.Pending, h.Ctx.BookingCancellations.Single().DebitNoteStatus);
    }

    // ============================================================
    // Anti-doble-cobro diferido (R13) — re-evaluado en runtime
    // ============================================================

    [Fact]
    public async Task PenaltyDeductionLoadedAfterDay0_RoutesToManualReview()
    {
        var h = BuildService();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);

        // Simular que entre el Dia 0 y el Dia N se cargo una deduction CancellationPenalty
        // en el refund del operador para este BC. El gating diferido debe re-chequearlo en
        // runtime y rutear a ManualReview (no emitir).
        var refund = new OperatorRefundReceived
        {
            SupplierId = bc.SupplierId,
            ReceivedAmount = 50_000m,
            Currency = "ARS",
            ReceivedAt = DateTime.UtcNow,
            ReceivedByUserId = "cashier",
        };
        h.Ctx.Set<OperatorRefundReceived>().Add(refund);
        await h.Ctx.SaveChangesAsync();

        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = h.Ctx.BookingCancellations.Single().Id,
            GrossAmount = 50_000m,
            NetAmount = 20_000m,
            CreatedByUserId = "cashier",
        };
        allocation.Deductions.Add(new DeductionLine
        {
            Kind = DeductionKind.CancellationPenalty,
            Amount = 30_000m,
        });
        h.Ctx.Set<OperatorRefundAllocation>().Add(allocation);
        await h.Ctx.SaveChangesAsync();

        await h.Service.ConfirmPenaltyAsync(bcId, Request(), "u", "U", false, default,
            userCanClassifyAgencyPenalty: true);

        // La penalidad quedo Confirmed (marca persistida) pero la ND NO se emitio: el motor
        // la ruteo a ManualReview por la disyuncion anti-doble-cobro.
        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(PenaltyStatus.Confirmed, after.PenaltyStatus);
        Assert.Equal(DebitNoteStatus.ManualReview, after.DebitNoteStatus);
        Assert.Null(after.DebitNoteInvoiceId);
    }

    [Fact]
    public async Task PenaltyAboveOriginalTotal_RoutesToManualReview_NoDebitNote()
    {
        var h = BuildService();
        // Factura original de 100.000; el operador confirma una penalidad de 150.000 (supera
        // el total). El gating heredado de ADR-013 (BuildManualReviewReason: penalidad >
        // ImporteTotal) debe rutear a ManualReview en el camino diferido, sin emitir la ND.
        // El monto sigue por debajo del umbral de 4-eyes (2.000.000), asi que el 4-eyes no
        // intercepta antes; lo que decide es el gating de monto.
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx, originalTotal: 100_000m);
        SetupCreateEmitsDebitNote(h);

        await h.Service.ConfirmPenaltyAsync(bcId, Request(amount: 150_000m),
            "u", "U", false, default, userCanClassifyAgencyPenalty: true);

        // La marca Confirmed se persiste (exactly-once), pero la ND NO se emite: queda en
        // ManualReview para que el back-office la revise.
        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(PenaltyStatus.Confirmed, after.PenaltyStatus);
        Assert.Equal(DebitNoteStatus.ManualReview, after.DebitNoteStatus);
        Assert.Null(after.DebitNoteInvoiceId);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // Plazo de la ND (M4) — alerta NO bloqueante
    // ============================================================

    [Fact]
    public async Task ConfirmedPastGraceDays_EmitsAnyway_AndLogsLateWarning()
    {
        var h = BuildService();
        // ConfirmedWithClientAt = hoy-10. La cancelacion fue hace mucho; el operador confirma
        // 20 dias atras (> GraceDays=15 pero <= HardWarnDays=60). Para que la fecha sea valida
        // (no anterior a la cancelacion) movemos ConfirmedWithClientAt mas atras.
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);
        var bc = h.Ctx.BookingCancellations.Single();
        bc.ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-40);
        await h.Ctx.SaveChangesAsync();
        SetupCreateEmitsDebitNote(h);

        await h.Service.ConfirmPenaltyAsync(bcId, Request(date: DateTime.UtcNow.AddDays(-20)),
            "u", "U", false, default, userCanClassifyAgencyPenalty: true);

        // NO bloquea: la ND igual se emite (Pending) pese a estar fuera del plazo de gracia.
        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(DebitNoteStatus.Pending, after.DebitNoteStatus);
        // Y queda registrado el warning de plazo (metric:cancellation_debit_note_late).
        Assert.Contains(h.Log.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("cancellation_debit_note_late"));
    }

    [Fact]
    public async Task ConfirmedPastHardWarnDays_EmitsAnyway_AndLogsVeryLateWarning()
    {
        var h = BuildService();
        // El operador confirma 70 dias atras (> HardWarnDays=60) -> warning elevado.
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);
        var bc = h.Ctx.BookingCancellations.Single();
        bc.ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-90);
        await h.Ctx.SaveChangesAsync();
        SetupCreateEmitsDebitNote(h);

        await h.Service.ConfirmPenaltyAsync(bcId, Request(date: DateTime.UtcNow.AddDays(-70)),
            "u", "U", false, default, userCanClassifyAgencyPenalty: true);

        // NO bloquea: emite igual.
        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(DebitNoteStatus.Pending, after.DebitNoteStatus);
        // Warning del umbral duro (metric:cancellation_debit_note_very_late).
        Assert.Contains(h.Log.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("cancellation_debit_note_very_late"));
    }

    // ============================================================
    // Bandeja — ND huerfana (B1 pieza 3)
    // ============================================================

    [Fact]
    public async Task OrphanDebitNote_BandejaRelinks_DoesNotReemit()
    {
        var h = BuildService();
        var (_, bc, original) = await SeedPostNcAsync(h.Ctx);
        // Simular crash entre T1 (ND creada) y T2 (vincular): PenaltyStatus=Confirmed,
        // DebitNoteInvoiceId=null, pero existe una ND para la factura original.
        var bcEntity = h.Ctx.BookingCancellations.Single();
        bcEntity.PenaltyStatus = PenaltyStatus.Confirmed;
        bcEntity.PenaltyAmountAtEvent = 30_000m;
        await h.Ctx.SaveChangesAsync();

        var orphanNd = new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = 200,
            Resultado = "A",
            CAE = "55555555",
            ReservaId = original.ReservaId,
            OriginalInvoiceId = original.Id,
        };
        h.Ctx.Invoices.Add(orphanNd);
        await h.Ctx.SaveChangesAsync();

        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);

        // La bandeja re-vinculo la ND huerfana (NO emitio otra). Como obtuvo CAE (Issued),
        // ya no aparece en la bandeja como pendiente.
        var relinked = h.Ctx.BookingCancellations.Single();
        Assert.Equal(orphanNd.Id, relinked.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Issued, relinked.DebitNoteStatus);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // ADR-044 (fix choque con ADR-014, 2026-07-14) — el re-vinculador de ND huerfana es MAS VIEJO
    // (2026-07-08) que el flujo de "deshacer una multa ya emitida" (2026-07-14). Ambos comparten el
    // MISMO perfil de deteccion ("BC Confirmed sin ND vinculada"): antes, ese perfil solo podia ser un
    // crash entre crear la ND y vincularla; ahora TAMBIEN puede ser un BC recien deshecho A PROPOSITO.
    // Los tests de abajo prueban que la tabla hija BookingCancellationDebitNoteAnnulment desambigua
    // los dos casos, y que un estado ya corrompido (el limbo real de produccion, BC 13 de la reserva
    // F-2026-1043) se auto-repara solo con abrir la bandeja.
    // ============================================================

    [Fact]
    public async Task OrphanDebitNote_WithSucceededAnnulment_IsNotRelinked()
    {
        // Esta ND YA fue anulada por su propia Nota de Credito (Status=Succeeded): es una ND MUERTA, no una
        // ND huerfana valida. Si el re-vinculador la re-atara, el paso volveria a mostrar "multa cobrada"
        // sobre un comprobante que ya no vale nada fiscalmente — exactamente el limbo real de produccion.
        var h = BuildService();
        var (_, _, original) = await SeedPostNcAsync(h.Ctx);
        var bcEntity = h.Ctx.BookingCancellations.Single();
        bcEntity.PenaltyStatus = PenaltyStatus.Confirmed;
        bcEntity.PenaltyAmountAtEvent = 30_000m;
        await h.Ctx.SaveChangesAsync();

        var undoneNd = new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = 200,
            Resultado = "A",
            CAE = "55555555",
            ReservaId = original.ReservaId,
            OriginalInvoiceId = original.Id,
        };
        h.Ctx.Invoices.Add(undoneNd);
        await h.Ctx.SaveChangesAsync();

        h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Add(new BookingCancellationDebitNoteAnnulment
        {
            BookingCancellationId = bcEntity.Id,
            AnnulledDebitNoteInvoiceId = undoneNd.Id,
            Status = DebitNoteAnnulmentStatus.Succeeded,
            Reason = "La multa estaba mal calculada.",
            Amount = 30_000m,
            Currency = "ARS",
            RequestedByUserId = "u",
            RequestedByUserName = "U",
        });
        await h.Ctx.SaveChangesAsync();

        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);

        var afterQuery = h.Ctx.BookingCancellations.Single();
        Assert.Null(afterQuery.DebitNoteInvoiceId); // NO se re-engancho la ND muerta.
        // El paso queda visible para re-disparo manual (Reintentar/Corregir/Emitir), NO re-vinculado en
        // silencio a un comprobante que ya no existe fiscalmente.
        Assert.Contains(rows, r =>
            r.BookingCancellationPublicId == afterQuery.PublicId &&
            r.DebitNoteStatus == CancellationDebitNotePendingDto.ConfirmedWithoutDebitNotePseudoStatus);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OrphanDebitNote_WithPendingAnnulment_IsNotRelinked()
    {
        // Control simetrico al de arriba: mientras la NC-anula-ND sigue en vuelo (Pending, todavia sin
        // CAE), la ND tampoco se re-engancha. Si se re-atara ahora y despues ARCA aprobara la NC, el
        // reconciliador (DebitNoteAnnulmentReconciliation) desvincularia una ND que el re-vinculador
        // acababa de re-atar — dos escritores pisandose el mismo campo.
        var h = BuildService();
        var (_, _, original) = await SeedPostNcAsync(h.Ctx);
        var bcEntity = h.Ctx.BookingCancellations.Single();
        bcEntity.PenaltyStatus = PenaltyStatus.Confirmed;
        bcEntity.PenaltyAmountAtEvent = 30_000m;
        await h.Ctx.SaveChangesAsync();

        var ndBeingUndone = new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = 200,
            Resultado = "A",
            CAE = "55555555",
            ReservaId = original.ReservaId,
            OriginalInvoiceId = original.Id,
        };
        h.Ctx.Invoices.Add(ndBeingUndone);
        await h.Ctx.SaveChangesAsync();

        h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Add(new BookingCancellationDebitNoteAnnulment
        {
            BookingCancellationId = bcEntity.Id,
            AnnulledDebitNoteInvoiceId = ndBeingUndone.Id,
            Status = DebitNoteAnnulmentStatus.Pending,
            Reason = "La multa estaba mal calculada.",
            Amount = 30_000m,
            Currency = "ARS",
            RequestedByUserId = "u",
            RequestedByUserName = "U",
        });
        await h.Ctx.SaveChangesAsync();

        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);

        var afterQuery = h.Ctx.BookingCancellations.Single();
        Assert.Null(afterQuery.DebitNoteInvoiceId);
        Assert.Contains(rows, r =>
            r.BookingCancellationPublicId == afterQuery.PublicId &&
            r.DebitNoteStatus == CancellationDebitNotePendingDto.ConfirmedWithoutDebitNotePseudoStatus);
    }

    [Fact]
    public async Task WronglyRelinkedDebitNote_IsAutoRepaired_ReopeningTheStep()
    {
        // Reproduce el limbo REAL de produccion (reserva F-2026-1043 / BC 13, 2026-07-14): antes de este
        // fix, el re-vinculador (ADR-014) corrio DESPUES de que la ND ya habia sido desvinculada por la
        // reconciliacion del "deshacer" y la volvio a atar por error. El BC queda con DebitNoteInvoiceId
        // apuntando a una ND MUERTA (ya anulada, Status=Succeeded en la tabla hija). La bandeja tiene que
        // detectar y reparar esto solo con abrirse, sin intervencion manual.
        var h = BuildService();
        var (_, _, original) = await SeedPostNcAsync(h.Ctx);

        var undoneNd = new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = 200,
            Resultado = "A",
            CAE = "55555555",
            ReservaId = original.ReservaId,
            OriginalInvoiceId = original.Id,
        };
        h.Ctx.Invoices.Add(undoneNd);
        await h.Ctx.SaveChangesAsync();

        var bcEntity = h.Ctx.BookingCancellations.Single();
        // Estado CORRUPTO (el bug real): el BC quedo re-enganchado a la ND que ya fue anulada.
        bcEntity.PenaltyStatus = PenaltyStatus.Confirmed;
        bcEntity.PenaltyAmountAtEvent = 30_000m;
        bcEntity.DebitNoteInvoiceId = undoneNd.Id;
        bcEntity.DebitNoteStatus = DebitNoteStatus.Issued;
        await h.Ctx.SaveChangesAsync();

        h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Add(new BookingCancellationDebitNoteAnnulment
        {
            BookingCancellationId = bcEntity.Id,
            AnnulledDebitNoteInvoiceId = undoneNd.Id,
            Status = DebitNoteAnnulmentStatus.Succeeded,
            Reason = "La multa estaba mal calculada.",
            Amount = 30_000m,
            Currency = "ARS",
            RequestedByUserId = "u",
            RequestedByUserName = "U",
        });
        await h.Ctx.SaveChangesAsync();

        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);

        var repaired = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bcEntity.Id);
        Assert.Null(repaired.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.NotApplicable, repaired.DebitNoteStatus);
        Assert.Null(repaired.DebitNoteArcaErrorMessage);

        // El paso vuelve a abierto: aparece en la bandeja para re-disparo manual, tal como espera el
        // usuario despues de deshacer una multa.
        Assert.Contains(rows, r =>
            r.BookingCancellationPublicId == repaired.PublicId &&
            r.DebitNoteStatus == CancellationDebitNotePendingDto.ConfirmedWithoutDebitNotePseudoStatus);

        // Rastro de auditoria dedicado (accion propia, para que el contador pueda filtrar esta reparacion).
        h.AuditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.OperatorPenaltyDebitNoteOrphanLinkRepaired,
            AuditActions.BookingCancellationEntityName,
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string>(),
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmedWithoutDebitNote_AppearsInBandejaForManualRetrigger()
    {
        var h = BuildService();
        var (_, _, _) = await SeedPostNcAsync(h.Ctx);
        // PenaltyStatus=Confirmed pero NO existe ND para la factura original.
        var bc = h.Ctx.BookingCancellations.Single();
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.PenaltyAmountAtEvent = 30_000m;
        await h.Ctx.SaveChangesAsync();

        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);

        Assert.Contains(rows, r =>
            r.BookingCancellationPublicId == bc.PublicId &&
            r.DebitNoteStatus == CancellationDebitNotePendingDto.ConfirmedWithoutDebitNotePseudoStatus);
    }

    // ============================================================
    // Bandeja — penalidad ESTIMADA de cargo propio (M-B2, caso dominante)
    // ============================================================

    [Fact]
    public async Task EstimatedAgencyOwnedWithCreditNote_AppearsInBandejaForConfirmation()
    {
        var h = BuildService();
        var (_, _, _) = await SeedPostNcAsync(h.Ctx);
        // Caso dominante: cargo propio de la agencia, penalidad ESTIMADA (el operador todavia
        // no confirmo el monto), NC total ya emitida, sin ND. Debe aparecer en la bandeja con
        // el pseudo-estado nuevo para que el frontend abra el ConfirmPenaltyModal.
        var bc = h.Ctx.BookingCancellations.Single();
        bc.ConceptKind = CancellationConceptKind.AgencyCancellationFee;
        bc.PenaltyStatus = PenaltyStatus.Estimated;
        bc.PenaltyAmountAtEvent = 30_000m;
        await h.Ctx.SaveChangesAsync();

        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);

        Assert.Contains(rows, r =>
            r.BookingCancellationPublicId == bc.PublicId &&
            r.DebitNoteStatus == CancellationDebitNotePendingDto.EstimatedPendingConfirmationPseudoStatus);
    }

    [Fact]
    public async Task EstimatedAgencyManagementFee_AlsoAppearsInBandeja()
    {
        var h = BuildService();
        var (_, _, _) = await SeedPostNcAsync(h.Ctx);
        // El otro concepto agency-owned (cargo de gestion) tambien debe listarse.
        var bc = h.Ctx.BookingCancellations.Single();
        bc.ConceptKind = CancellationConceptKind.AgencyManagementFee;
        bc.PenaltyStatus = PenaltyStatus.Estimated;
        await h.Ctx.SaveChangesAsync();

        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);

        Assert.Contains(rows, r =>
            r.BookingCancellationPublicId == bc.PublicId &&
            r.DebitNoteStatus == CancellationDebitNotePendingDto.EstimatedPendingConfirmationPseudoStatus);
    }

    [Fact]
    public async Task EstimatedPassThrough_DoesNotAppearInBandeja()
    {
        var h = BuildService();
        var (_, _, _) = await SeedPostNcAsync(h.Ctx);
        // Pass-through (operador retiene la penalidad): NUNCA lleva ND, asi que aunque la
        // penalidad este Estimated y la NC ya este emitida, NO debe aparecer en la bandeja.
        var bc = h.Ctx.BookingCancellations.Single();
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.PenaltyStatus = PenaltyStatus.Estimated;
        await h.Ctx.SaveChangesAsync();

        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);

        Assert.DoesNotContain(rows, r => r.BookingCancellationPublicId == bc.PublicId);
    }

    [Fact]
    public async Task ConfirmedAgencyOwnedWithDebitNoteLinked_DoesNotAppearAsEstimated()
    {
        var h = BuildService();
        var (_, _, original) = await SeedPostNcAsync(h.Ctx);
        // Una vez emitida y vinculada la ND (DebitNoteInvoiceId seteado), el BC ya NO debe
        // listarse como "estimado pendiente": el filtro exige DebitNoteInvoiceId == null.
        var nd = new Invoice
        {
            TipoComprobante = 12,
            PuntoDeVenta = 1,
            NumeroComprobante = 300,
            Resultado = "A",
            CAE = "77777777",
            ReservaId = original.ReservaId,
            OriginalInvoiceId = original.Id,
        };
        h.Ctx.Invoices.Add(nd);
        await h.Ctx.SaveChangesAsync();

        var bc = h.Ctx.BookingCancellations.Single();
        bc.ConceptKind = CancellationConceptKind.AgencyCancellationFee;
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.DebitNoteInvoiceId = nd.Id;
        bc.DebitNoteStatus = DebitNoteStatus.Issued;
        await h.Ctx.SaveChangesAsync();

        var rows = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);

        Assert.DoesNotContain(rows, r =>
            r.BookingCancellationPublicId == bc.PublicId &&
            r.DebitNoteStatus == CancellationDebitNotePendingDto.EstimatedPendingConfirmationPseudoStatus);
    }

    // ============================================================
    // CAMBIO 1 (2026-06-24): Admin bypasea el 4-eyes de confirm-penalty
    // ============================================================

    [Fact]
    public async Task AdminBypassesFourEyes_WithReason_EmitsAndLogsSelfAuthorized()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);

        // Sin soporte documental (normalmente exige 4-eyes), pero el actor es Admin -> bypass.
        // OverrideReason no vacio es obligatorio para el bypass.
        var request = Request(amount: 30_000m, support: null) with { OverrideReason = "Dueno confirma multa, unico admin" };

        var dto = await h.Service.ConfirmPenaltyAsync(
            bcId, request, "owner", "Owner", requesterIsAdmin: true, default,
            userCanClassifyAgencyPenalty: true);

        // Emitio la ND sin pedir approval.
        Assert.Equal(DebitNoteStatus.Pending, h.Ctx.BookingCancellations.Single().DebitNoteStatus);

        // Quedo el rastro de auditoria AdminSelfAuthorized.
        h.AuditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.AdminSelfAuthorized,
            AuditActions.BookingCancellationEntityName,
            It.IsAny<string>(),
            It.IsAny<string?>(),
            "owner",
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdminBypassesFourEyes_WithoutReason_Rejected()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);

        // Admin sin OverrideReason -> rechazo (exigimos motivo para dejar rastro al contador).
        var request = Request(amount: 30_000m, support: null); // OverrideReason null por defecto

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bcId, request, "owner", "Owner", requesterIsAdmin: true, default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADMIN-SELFAUTH", ex.InvariantCode);
    }

    [Fact]
    public async Task NonAdmin_StillRequiresFourEyes_NoBypass()
    {
        var h = BuildService();
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx);

        // Un no-Admin con OverrideReason pero sin approval valido sigue rebotando (la maquinaria
        // de 4-eyes NO se borro: el bypass es SOLO para Admin).
        var request = Request(amount: 30_000m, support: null) with { OverrideReason = "intento de vendedor" };

        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bcId, request, "vendedor", "V", requesterIsAdmin: false, default,
                userCanClassifyAgencyPenalty: true));
    }

    // ============================================================
    // CAMBIO 3 (2026-06-24): captura de la MONEDA de la multa del operador
    // ============================================================

    [Fact]
    public async Task ConfirmPenalty_PersistsExplicitPenaltyCurrencyOnLines()
    {
        var h = BuildService();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);
        await SeedLineAsync(h.Ctx, bc, lineCurrency: "ARS");

        // El operador retuvo la multa en USD aunque el servicio sea ARS.
        var request = Request(amount: 30_000m) with { PenaltyCurrency = "usd" };

        await h.Service.ConfirmPenaltyAsync(
            bcId, request, "u", "U", requesterIsAdmin: false, default,
            userCanClassifyAgencyPenalty: true);

        var line = h.Ctx.BookingCancellationLines.Single(l => l.BookingCancellationId == bc.Id);
        Assert.Equal("USD", line.PenaltyCurrency); // normalizado a mayusculas
    }

    [Fact]
    public async Task ConfirmPenalty_DefaultsPenaltyCurrencyToLineCurrency_WhenNotProvided()
    {
        var h = BuildService();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx);
        SetupCreateEmitsDebitNote(h);
        await SeedLineAsync(h.Ctx, bc, lineCurrency: "USD");

        // Request sin PenaltyCurrency -> usa la moneda de la linea/servicio.
        var request = Request(amount: 30_000m); // PenaltyCurrency null por defecto

        await h.Service.ConfirmPenaltyAsync(
            bcId, request, "u", "U", requesterIsAdmin: false, default,
            userCanClassifyAgencyPenalty: true);

        var line = h.Ctx.BookingCancellationLines.Single(l => l.BookingCancellationId == bc.Id);
        Assert.Equal("USD", line.PenaltyCurrency);
    }

    // ============================================================
    // ATOMICIDAD del reconciler + BLINDAJE de la emision (fix 2026-07-01)
    // ============================================================

    /// <summary>
    /// Test (a): si el RECONCILER del pool de saldo a favor del operador tira (INV-SUPCREDIT-001), la
    /// confirmacion NO debe quedar a medias: la marca PenaltyStatus=Confirmed NO se persiste, NO se emite ND, y
    /// el error sale limpio (409 de negocio). Antes del fix, la marca se committeaba ANTES del reconciler y la
    /// reserva quedaba trabada.
    ///
    /// <para>Forzamos el throw sembrando un saldo a favor del operador YA aplicado (RemainingBalance=0): como el
    /// sobrepago actual es 0, el reconciler querria drenar el credito pero no puede -> INV-SUPCREDIT-001.</para>
    /// </summary>
    [Fact]
    public async Task ReconcilerThrows_RollsBackConfirmation_NoDebitNote()
    {
        var h = BuildService();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx, ownership: PenaltyOwnership.Agency);
        SetupCreateEmitsDebitNote(h);

        // Saldo a favor del operador ya consumido (aplicado a otra reserva): CreditedAmount 100k, Remaining 0.
        h.Ctx.SupplierCreditEntries.Add(new SupplierCreditEntry
        {
            SupplierId = bc.SupplierId,
            Currency = "ARS",
            CreditedAmount = 100_000m,
            RemainingBalance = 0m,
            IsFullyConsumed = true,
            CreatedAt = DateTime.UtcNow,
        });
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(bcId, Request(amount: 30_000m), "u", "U", false, default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-SUPCREDIT-001", ex.InvariantCode);

        // ATOMICIDAD: la marca de no-retorno NO quedo persistida (leemos el estado DURABLE con AsNoTracking).
        var durable = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Estimated, durable.PenaltyStatus);
        Assert.Equal(DebitNoteStatus.NotApplicable, durable.DebitNoteStatus);
        Assert.Null(durable.OperatorPenaltyConfirmedDate);

        // No se emitio ninguna ND (nunca se llego al paso de emision).
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Test (b): si la EMISION de la ND lanza una excepcion (ej. "ya hay una factura en proceso"), la multa
    /// queda Confirmed (durable) + la ND en ManualReview, la respuesta es EXITO-con-aviso (NO un 500), y la
    /// bandeja de NDs por revisar la levanta para poder destrabarla.
    /// </summary>
    [Fact]
    public async Task EmissionThrows_KeepsConfirmed_RoutesManualReview_SuccessAndTrayLevantaIt()
    {
        var h = BuildService();
        var (bcId, bc, _) = await SeedPostNcAsync(h.Ctx, ownership: PenaltyOwnership.Operator);

        // La emision de la ND falla (factura en vuelo, ARCA rebota, etc.).
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Ya hay una factura en proceso para esta reserva."));

        // NO tira: exito-con-aviso.
        var dto = await h.Service.ConfirmPenaltyAsync(bcId, Request(concept: null), "u", "U", false, default,
            userCanClassifyAgencyPenalty: true);

        // Estado DURABLE: multa confirmada + ND en revision manual, sin link.
        var durable = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Confirmed, durable.PenaltyStatus);
        Assert.Equal(DebitNoteStatus.ManualReview, durable.DebitNoteStatus);
        Assert.Null(durable.DebitNoteInvoiceId);
        Assert.Equal("ManualReview", dto.DebitNoteStatus);

        // La bandeja de NDs por revisar la levanta (rama huerfana: Confirmed + link nulo).
        var tray = await h.Service.GetCancellationsWithMissingDebitNoteAsync(default);
        Assert.Contains(tray, r => r.BookingCancellationPublicId == bc.PublicId);
    }

    // ============================================================
    // Recuperacion: retry-debit-note (destrabar #F-2026-1025)
    // ============================================================

    /// <summary>
    /// Construye el estado real de #F-2026-1025 corriendo <c>ConfirmPenaltyAsync</c> con la emision FALLANDO:
    /// la multa queda Confirmed (durable) + la ND en ManualReview, sin link, con TODOS los campos de
    /// auditoria/purpose seteados por el flujo real (que el gating de la ND exige). Devuelve el PublicId del BC.
    /// </summary>
    private static async Task<Guid> SeedConfirmedButDebitNoteFailedAsync(Harness h)
    {
        var (bcId, _, _) = await SeedPostNcAsync(h.Ctx, ownership: PenaltyOwnership.Operator);
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Ya hay una factura en proceso para esta reserva."));
        await h.Service.ConfirmPenaltyAsync(bcId, Request(concept: null), "u", "U", false, default,
            userCanClassifyAgencyPenalty: true);
        return bcId;
    }

    /// <summary>Reintento sobre una cancelacion Confirmed+sin-ND (estado real #F-2026-1025): emite la ND de cero.</summary>
    [Fact]
    public async Task RetryDebitNote_EmitsFreshWhenNoPriorNote()
    {
        var h = BuildService();
        var publicId = await SeedConfirmedButDebitNoteFailedAsync(h);

        // Ahora la emision funciona: el reintento emite la ND.
        SetupCreateEmitsDebitNote(h);
        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            publicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        var after = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync();
        Assert.Equal(DebitNoteStatus.Pending, after.DebitNoteStatus);
        Assert.NotNull(after.DebitNoteInvoiceId);
    }

    /// <summary>Reintento cuando ya existe una ND creada sin vincular: la RE-VINCULA, NO emite otra (anti doble).</summary>
    [Fact]
    public async Task RetryDebitNote_RelinksExistingOrphan_NoDoubleEmission()
    {
        var h = BuildService();
        var (_, bc, original) = await SeedPostNcAsync(h.Ctx, ownership: PenaltyOwnership.Operator);
        SetupCreateEmitsDebitNote(h);

        var seeded = h.Ctx.BookingCancellations.Single();
        seeded.PenaltyStatus = PenaltyStatus.Confirmed;
        seeded.PenaltyAmountAtEvent = 30_000m;
        await h.Ctx.SaveChangesAsync();

        // Ya existe una ND (con CAE) para la factura original: un intento previo la creo pero no la vinculo.
        var orphanNd = new Invoice
        {
            TipoComprobante = 12,
            PuntoDeVenta = 1,
            NumeroComprobante = 200,
            Resultado = "A",
            CAE = "55555555",
            ReservaId = original.ReservaId,
            OriginalInvoiceId = original.Id,
        };
        h.Ctx.Invoices.Add(orphanNd);
        await h.Ctx.SaveChangesAsync();

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        var after = await h.Ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(orphanNd.Id, after.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Issued, after.DebitNoteStatus);
        // NO se emitio otra ND.
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Reintento sin permiso -> rebota INV-ADR014-RETRY-PERM (gate fiscal server-side).</summary>
    [Fact]
    public async Task RetryDebitNote_WithoutPermission_Rejects()
    {
        var h = BuildService();
        var (_, bc, _) = await SeedPostNcAsync(h.Ctx, ownership: PenaltyOwnership.Operator);
        var seeded = h.Ctx.BookingCancellations.Single();
        seeded.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.RetryDebitNoteEmissionAsync(bc.PublicId, "u", "U", default,
                userCanClassifyAgencyPenalty: false));
        Assert.Equal("INV-ADR014-RETRY-PERM", ex.InvariantCode);
    }

    /// <summary>Reintento cuando la multa aun NO esta confirmada -> rebota INV-ADR014-RETRY-001.</summary>
    [Fact]
    public async Task RetryDebitNote_RejectsWhenNotConfirmed()
    {
        var h = BuildService();
        var (_, bc, _) = await SeedPostNcAsync(h.Ctx, ownership: PenaltyOwnership.Operator);
        // Penalidad sigue Estimated (default).

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.RetryDebitNoteEmissionAsync(bc.PublicId, "u", "U", default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-RETRY-001", ex.InvariantCode);
    }

    /// <summary>Reintento cuando la ND ya esta vinculada -> rebota INV-ADR014-RETRY-002 (no re-emite).</summary>
    [Fact]
    public async Task RetryDebitNote_RejectsWhenAlreadyLinked()
    {
        var h = BuildService();
        var (_, bc, original) = await SeedPostNcAsync(h.Ctx, ownership: PenaltyOwnership.Operator);
        var nd = new Invoice
        {
            TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 300, Resultado = "A", CAE = "77777777",
            ReservaId = original.ReservaId, OriginalInvoiceId = original.Id,
        };
        h.Ctx.Invoices.Add(nd);
        await h.Ctx.SaveChangesAsync();

        var seeded = h.Ctx.BookingCancellations.Single();
        seeded.PenaltyStatus = PenaltyStatus.Confirmed;
        seeded.DebitNoteInvoiceId = nd.Id;
        seeded.DebitNoteStatus = DebitNoteStatus.Issued;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.RetryDebitNoteEmissionAsync(bc.PublicId, "u", "U", default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-RETRY-002", ex.InvariantCode);
    }

    /// <summary>Si el reintento vuelve a fallar la emision, sigue devolviendo EXITO (ManualReview), no un 500.</summary>
    [Fact]
    public async Task RetryDebitNote_EmissionThrowsAgain_StillSucceedsWithManualReview()
    {
        var h = BuildService();
        // El mock ya esta configurado para FALLAR la emision (dentro del helper) y sigue fallando en el reintento.
        var publicId = await SeedConfirmedButDebitNoteFailedAsync(h);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            publicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var after = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync();
        Assert.Equal(DebitNoteStatus.ManualReview, after.DebitNoteStatus);
        Assert.Null(after.DebitNoteInvoiceId);
    }

    /// <summary>Agrega una linea hija al BC con la moneda indicada (helper para los tests de CAMBIO 3).</summary>
    private static async Task SeedLineAsync(AppDbContext ctx, BookingCancellation bc, string lineCurrency)
    {
        var supplierId = ctx.Suppliers.First().Id;
        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = supplierId,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Full,
            Currency = lineCurrency,
            LineSaleAmount = 50_000m,
        });
        await ctx.SaveChangesAsync();
    }
}
