using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-012/013 (multimoneda ND, 2026-07-08): la Nota de Debito por multa de anulacion se emite en MONEDA
/// EXTRANJERA (USD) heredando la moneda y el tipo de cambio CONGELADOS de la factura original, con los MISMOS
/// guards multimoneda que ya usa la NC total (flag EnableMultiCurrencyInvoicing ON + TC coherente + moneda
/// soportada). Antes, cualquier factura no-ARS ruteaba a revision manual.
///
/// <para>Estos tests UNIT usan EF InMemory con un <see cref="IInvoiceService"/> MOCKEADO: la validacion real de
/// moneda (ValidateMultiCurrencyInvoicingAsync) es responsabilidad de InvoiceService y se homologa aparte. Aca
/// verificamos la LOGICA del servicio de cancelacion: (1) que el gating deje pasar la divisa con el flag ON,
/// (2) que el request de la ND se arme con MonId/MonCotiz + trazabilidad del TC HEREDADOS del original, y
/// (3) que el retry desde la bandeja RE-EVALUE un caso que quedo en revision manual por moneda.</para>
/// </summary>
public class CancellationDebitNoteMultiCurrencyTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"nd-multicurrency-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Builder del service. <paramref name="multiCurrencyOn"/> prende EnableMultiCurrencyInvoicing (el MISMO flag
    /// que gobierna la NC total; NO es un flag nuevo). El InvoiceMock CAPTURA el ultimo CreateInvoiceRequest para
    /// poder afirmar la moneda/TC/trazabilidad que se armo, e inserta una Invoice real para que la vinculacion la
    /// resuelva.
    /// </summary>
    private static (BookingCancellationService Service, Mock<IInvoiceService> InvoiceMock, CapturedRequest Captured) BuildService(
        AppDbContext ctx, bool debitNoteOn = true, bool multiCurrencyOn = true)
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
                EnableCancellationDebitNote = debitNoteOn,
                EnableMultiCurrencyInvoicing = multiCurrencyOn,
                OperatorRefundTimeoutDays = 60,
                CancellationDebitNoteFourEyesThreshold = 1_000_000m,
                CancellationDebitNoteGraceDays = 30,
                CancellationDebitNoteHardWarnDays = 90,
            });

        var captured = new CapturedRequest();
        invoiceMock
            .Setup(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                captured.Request = req; // guardamos el request tal cual lo armo TryEmitCancellationDebitNoteAsync
                var nd = new Invoice
                {
                    PublicId = Guid.NewGuid(),
                    TipoComprobante = 12, // ND C
                    Resultado = "A",
                    MonId = req.MonId,
                    MonCotiz = req.MonCotiz,
                    ImporteTotal = req.Items.Sum(i => i.Total),
                };
                ctx.Invoices.Add(nd);
                ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalMock.Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            calculatorMock.Object, adminCountMock.Object);

        return (service, invoiceMock, captured);
    }

    /// <summary>Contenedor mutable para capturar el request armado por el service (Moq no da callbacks tipados facil aca).</summary>
    private sealed class CapturedRequest
    {
        public CreateInvoiceRequest? Request { get; set; }
    }

    /// <summary>
    /// Siembra un BC POST-NC con factura original C en la MONEDA/TC indicados. Sirve tanto para el path de
    /// ConfirmPenaltyAsync (multa recien confirmada) como para el de retry (ya en revision manual).
    /// </summary>
    private static async Task<BookingCancellation> SeedPostNcBcAsync(
        AppDbContext ctx,
        string monId = "DOL",
        decimal monCotiz = 1500m,
        int originalTipo = 11,
        bool preConfirmedForRetry = false,
        DebitNoteStatus debitNoteStatus = DebitNoteStatus.NotApplicable,
        string? penaltyCurrencyAtEvent = null)
    {
        var customer = new Customer { FullName = "Cliente USD", IsActive = true };
        var supplier = new Supplier { Name = "Operador USD", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-USD", Name = "Reserva USD", PayerId = customer.Id, Status = EstadoReserva.PendingOperatorRefund };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // Factura original en moneda extranjera con su trazabilidad del TC (como exige la facturacion en divisa).
        var originating = new Invoice
        {
            TipoComprobante = originalTipo,
            PuntoDeVenta = 1, NumeroComprobante = 500, CAE = "111", Resultado = "A",
            ImporteTotal = 200_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
            MonId = monId,
            MonCotiz = monCotiz,
            ExchangeRateSource = ExchangeRateSource.Manual,
            ExchangeRateFetchedAt = DateTime.UtcNow.AddDays(-3),
            ExchangeRateJustification = "TC BNA vendedor divisa al momento de la factura",
        };
        ctx.Invoices.Add(originating);
        await ctx.SaveChangesAsync();

        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 501, CAE = "222", Resultado = "A",
            ImporteTotal = 200_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
            MonId = monId, MonCotiz = monCotiz,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = originating.Id,
            CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Anulacion con multa del operador (USD)",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-2),
            ConfirmedByUserId = "vendedor-1",
            AmountPaidAtCancellation = 200_000m,
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = monId == "DOL" ? "USD" : "ARS",
                AgencyTaxConditionAtEvent = "MONOTRIBUTO",
                SupplierTaxConditionAtEvent = "MONOTRIBUTO",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = monCotiz,
                FetchedAt = DateTime.UtcNow.AddDays(-2),
            },
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            DebitNoteStatus = debitNoteStatus,
        };

        if (preConfirmedForRetry)
        {
            // Estado del caso real BC 10: multa YA confirmada, ND sin emitir (quedo en revision manual por
            // moneda). El gating exige el rastro de auditoria, asi que lo dejamos sembrado.
            bc.PenaltyStatus = PenaltyStatus.Confirmed;
            bc.PenaltyAmountAtEvent = 30_000m;
            bc.PenaltyCurrencyAtEvent = penaltyCurrencyAtEvent; // puede venir vacio (multa confirmada antes del fix)
            bc.DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge;
            bc.ConceptClassifiedByUserId = "backoffice-1";
            bc.PenaltyConfirmedByUserId = "backoffice-1";
        }

        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();
        return bc;
    }

    // ============================================================
    // Emision via ConfirmPenaltyAsync
    // ============================================================

    [Fact]
    public async Task ConfirmPenalty_UsdInvoice_FlagOn_EmitsDebitNote_InheritsCurrencyAndRate()
    {
        using var ctx = NewDbContext();
        var bc = await SeedPostNcBcAsync(ctx, monId: "DOL", monCotiz: 1500m);
        var (service, invoiceMock, captured) = BuildService(ctx, debitNoteOn: true, multiCurrencyOn: true);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 30_000m,
            OperatorConfirmationDate: DateTime.UtcNow.Date,
            DebitNotePurpose: null,
            PenaltyCurrency: "USD", // el usuario declara que el operador retuvo la multa en dolares
            SupportingDocumentReference: "mail-operador-multa.pdf");

        await service.ConfirmPenaltyAsync(
            bc.PublicId, request, "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);

        // La ND en USD se emitio (vinculada + Pending); la moneda declarada quedo persistida en ISO.
        Assert.NotNull(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Pending, reloaded.DebitNoteStatus);
        Assert.Equal("USD", reloaded.PenaltyCurrencyAtEvent);

        // El request de la ND HEREDO la moneda, el TC y la trazabilidad del comprobante original.
        Assert.NotNull(captured.Request);
        Assert.Equal("DOL", captured.Request!.MonId);
        Assert.Equal(1500m, captured.Request.MonCotiz);
        Assert.True(captured.Request.IsDebitNote);
        Assert.Equal(ExchangeRateSource.Manual, captured.Request.ExchangeRateSource);
        Assert.NotNull(captured.Request.ExchangeRateFetchedAt);
        // N1: la justificacion heredada quedo prefijada como "TC heredado".
        Assert.Contains("TC heredado", captured.Request.ExchangeRateJustification);
    }

    [Fact]
    public async Task ConfirmPenalty_DeclaredArs_OnUsdInvoice_RoutesToManual_NoEmission()
    {
        // B1 (security): el usuario cargo la multa "en pesos" pero la factura esta en dolares. NO emitimos: la ND
        // saldria por el numero equivocado (~1500x). Va a revision manual con un motivo de negocio.
        using var ctx = NewDbContext();
        var bc = await SeedPostNcBcAsync(ctx, monId: "DOL", monCotiz: 1500m);
        var (service, invoiceMock, _) = BuildService(ctx, debitNoteOn: true, multiCurrencyOn: true);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 30_000m,
            OperatorConfirmationDate: DateTime.UtcNow.Date,
            PenaltyCurrency: "ARS", // declarada en pesos: NO coincide con la factura USD
            SupportingDocumentReference: "mail.pdf");

        await service.ConfirmPenaltyAsync(
            bc.PublicId, request, "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Null(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.ManualReview, reloaded.DebitNoteStatus);
        // El motivo persistido es de negocio, con etiquetas amigables y SIN codigos tecnicos ni numeros crudos.
        Assert.Contains("dólares (US$)", reloaded.DebitNoteArcaErrorMessage);
        Assert.Contains("pesos", reloaded.DebitNoteArcaErrorMessage);
        Assert.DoesNotContain("DOL", reloaded.DebitNoteArcaErrorMessage);
        invoiceMock.Verify(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmPenalty_UsdInvoice_FlagOff_RoutesToManual_NoEmission()
    {
        // No-regresion: con la facturacion multimoneda DESHABILITADA, una factura USD vuelve a revision manual
        // (como antes del fix). El flag EnableCancellationDebitNote sigue ON (el subsistema de multa esta vivo),
        // pero sin EnableMultiCurrencyInvoicing no emitimos comprobantes en divisa.
        using var ctx = NewDbContext();
        var bc = await SeedPostNcBcAsync(ctx, monId: "DOL", monCotiz: 1500m);
        var (service, invoiceMock, _) = BuildService(ctx, debitNoteOn: true, multiCurrencyOn: false);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 30_000m,
            OperatorConfirmationDate: DateTime.UtcNow.Date,
            PenaltyCurrency: "USD",
            SupportingDocumentReference: "mail.pdf");

        await service.ConfirmPenaltyAsync(
            bc.PublicId, request, "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Null(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.ManualReview, reloaded.DebitNoteStatus);
        Assert.Contains("dólares (US$)", reloaded.DebitNoteArcaErrorMessage);
        invoiceMock.Verify(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmPenalty_UsdInvoice_IncoherentRate_RoutesToManual_NoEmission()
    {
        // TC == 1 en una factura USD es incoherente (valuaria un dolar como un peso): revision manual aun con
        // el flag ON. Mismo candado que la NC total. El motivo NO muestra el numero crudo del TC.
        using var ctx = NewDbContext();
        var bc = await SeedPostNcBcAsync(ctx, monId: "DOL", monCotiz: 1m);
        var (service, invoiceMock, _) = BuildService(ctx, debitNoteOn: true, multiCurrencyOn: true);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 30_000m,
            OperatorConfirmationDate: DateTime.UtcNow.Date,
            PenaltyCurrency: "USD",
            SupportingDocumentReference: "mail.pdf");

        await service.ConfirmPenaltyAsync(
            bc.PublicId, request, "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Null(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.ManualReview, reloaded.DebitNoteStatus);
        Assert.Contains("dólares (US$)", reloaded.DebitNoteArcaErrorMessage);
        Assert.DoesNotContain("(1)", reloaded.DebitNoteArcaErrorMessage); // sin el numero crudo del TC
        invoiceMock.Verify(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmPenalty_ArsInvoice_FlagOn_StillEmits_ByteIdentical()
    {
        // No-regresion ARS: una factura en pesos emite la ND igual que siempre, con MonId "PES"/1. El fix
        // multimoneda no cambia el camino en pesos.
        using var ctx = NewDbContext();
        var bc = await SeedPostNcBcAsync(ctx, monId: "PES", monCotiz: 1m);
        var (service, _, captured) = BuildService(ctx, debitNoteOn: true, multiCurrencyOn: true);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 30_000m,
            OperatorConfirmationDate: DateTime.UtcNow.Date,
            PenaltyCurrency: "ARS",
            SupportingDocumentReference: "mail.pdf");

        await service.ConfirmPenaltyAsync(
            bc.PublicId, request, "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.NotNull(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Pending, reloaded.DebitNoteStatus);
        Assert.Equal("ARS", reloaded.PenaltyCurrencyAtEvent);
        Assert.NotNull(captured.Request);
        Assert.Equal("PES", captured.Request!.MonId);
        Assert.Equal(1m, captured.Request.MonCotiz);
    }

    // ============================================================
    // Retry desde la bandeja: re-evalua un ManualReview por moneda
    // ============================================================

    [Fact]
    public async Task Retry_ManualReviewByCurrency_DeclaredUsd_FlagOn_ReEvaluatesAndEmits()
    {
        // Multa confirmada CON moneda declarada (USD), ND en revision manual porque el gating viejo la ruteo por
        // moneda. Al reintentar desde la bandeja con el flag ON, se RE-EVALUA el gating y ahora emite en USD.
        using var ctx = NewDbContext();
        var bc = await SeedPostNcBcAsync(
            ctx, monId: "DOL", monCotiz: 1500m,
            preConfirmedForRetry: true,
            debitNoteStatus: DebitNoteStatus.ManualReview,
            penaltyCurrencyAtEvent: "USD");
        var (service, _, captured) = BuildService(ctx, debitNoteOn: true, multiCurrencyOn: true);

        await service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "backoffice-1", "Backoffice", CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.NotNull(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Pending, reloaded.DebitNoteStatus);
        Assert.Equal("USD", reloaded.PenaltyCurrencyAtEvent);
        Assert.NotNull(captured.Request);
        Assert.Equal("DOL", captured.Request!.MonId);
        Assert.Equal(1500m, captured.Request.MonCotiz);
    }

    [Fact]
    public async Task Retry_ManualReviewByCurrency_DeclaredNull_FlagOn_StaysManual_NoEmission()
    {
        // Caso real BC 10: multa confirmada ANTES del fix -> PenaltyCurrencyAtEvent VACIO. El retry directo NO
        // puede emitir (no sabemos en que moneda se cargo la multa): sigue en revision manual, conservador. El
        // dueño lo destraba con el circuito waive -> reabrir -> re-confirmar con la moneda (test aparte).
        using var ctx = NewDbContext();
        var bc = await SeedPostNcBcAsync(
            ctx, monId: "DOL", monCotiz: 1500m,
            preConfirmedForRetry: true,
            debitNoteStatus: DebitNoteStatus.ManualReview,
            penaltyCurrencyAtEvent: null);
        var (service, invoiceMock, _) = BuildService(ctx, debitNoteOn: true, multiCurrencyOn: true);

        await service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "backoffice-1", "Backoffice", CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Null(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.ManualReview, reloaded.DebitNoteStatus);
        Assert.Contains("No quedó registrado", reloaded.DebitNoteArcaErrorMessage);
        invoiceMock.Verify(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retry_ManualReviewByOtherReason_FlagOn_DoesNotEmit()
    {
        // Un ManualReview por un motivo distinto a la moneda (aca: factura original NO es C, es B=6) sigue en
        // revision manual al reintentar, aun con el flag ON: el fix multimoneda solo levanta el bloqueo por
        // moneda, no los demas controles fiscales.
        using var ctx = NewDbContext();
        var bc = await SeedPostNcBcAsync(
            ctx, monId: "DOL", monCotiz: 1500m, originalTipo: 6, // Factura B
            preConfirmedForRetry: true,
            debitNoteStatus: DebitNoteStatus.ManualReview,
            penaltyCurrencyAtEvent: "USD");
        var (service, invoiceMock, _) = BuildService(ctx, debitNoteOn: true, multiCurrencyOn: true);

        await service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "backoffice-1", "Backoffice", CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Null(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.ManualReview, reloaded.DebitNoteStatus);
        invoiceMock.Verify(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // Circuito de destrabe del caso real (BC 10): waive -> reabrir -> re-confirmar CON moneda -> emite
    // ============================================================

    [Fact]
    public async Task WaiveReopenReconfirmWithCurrency_EmitsDebitNote_ClosesTheLoop()
    {
        // Estado de BC 10: multa Confirmed, ND en revision manual, moneda declarada VACIA (confirmada antes del
        // fix), factura USD. El retry directo no emite (test anterior). El dueño la destraba asi:
        //   1) "No cobrar" la multa confirmada (waive, requiere Admin) -> queda cerrada sin multa.
        //   2) Reabrir el cierre (revert, Admin) -> vuelve a Estimated (pendiente).
        //   3) Re-confirmar la multa AHORA declarando la moneda (USD) -> auto-emite la ND en USD.
        using var ctx = NewDbContext();
        var bc = await SeedPostNcBcAsync(
            ctx, monId: "DOL", monCotiz: 1500m,
            preConfirmedForRetry: true,
            debitNoteStatus: DebitNoteStatus.ManualReview,
            penaltyCurrencyAtEvent: null);
        var (service, _, captured) = BuildService(ctx, debitNoteOn: true, multiCurrencyOn: true);

        // 1) No cobrar la multa (waive desde Confirmed -> requiere Admin).
        await service.WaiveOperatorPenaltyAsync(
            bc.PublicId, "El operador todavía no definió la multa; la cerramos para recargarla bien.",
            "admin-1", "Admin", CancellationToken.None,
            userCanClassifyAgencyPenalty: true, requesterIsAdmin: true);

        var afterWaive = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Waived, afterWaive.PenaltyStatus);

        // 2) Reabrir (revert, Admin) -> Estimated.
        await service.RevertWaivedOperatorPenaltyAsync(
            bc.PublicId, "El operador confirmó la multa; la reabrimos para cargarla con su moneda.",
            "admin-1", "Admin", requesterIsAdmin: true, CancellationToken.None);

        var afterRevert = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Estimated, afterRevert.PenaltyStatus);

        // 3) Re-confirmar declarando la moneda (USD) -> emite.
        var reconfirm = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 30_000m,
            OperatorConfirmationDate: DateTime.UtcNow.Date,
            PenaltyCurrency: "USD",
            SupportingDocumentReference: "acuerdo-operador.pdf");

        await service.ConfirmPenaltyAsync(
            bc.PublicId, reconfirm, "admin-1", "Admin",
            requesterIsAdmin: true, ct: CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Confirmed, reloaded.PenaltyStatus);
        Assert.NotNull(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Pending, reloaded.DebitNoteStatus);
        Assert.Equal("USD", reloaded.PenaltyCurrencyAtEvent);
        Assert.NotNull(captured.Request);
        Assert.Equal("DOL", captured.Request!.MonId);
        Assert.Equal(1500m, captured.Request.MonCotiz);
    }
}
