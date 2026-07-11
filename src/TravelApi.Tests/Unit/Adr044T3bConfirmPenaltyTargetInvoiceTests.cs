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
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 T3b Decision 1, gap de contrato del front (2026-07-10): <c>ConfirmPenaltyRequest.TargetInvoicePublicId</c>
/// — el usuario elige a que factura de venta activa corresponde el cargo automatico EN EL MISMO paso de
/// confirmar la multa, para cuando la reserva tiene 2+ facturas activas (ADR-042, ej. USD+ARS). Antes de este
/// campo, el cargo automatico SIEMPRE quedaba sin factura destino con 2+ facturas activas (nunca se podia elegir
/// al confirmar; solo se podia corregir DESPUES con <c>SetOperatorChargeTargetInvoiceAsync</c>).
///
/// <para>Reusa el mismo helper de siembra "pre-confirm" (linea con <c>RefundCap</c> &gt; 0, <c>PenaltyStatus</c>
/// aun <c>Estimated</c>) para que <c>AllocateConfirmedPenaltyToLinesAsync</c> ejecute el camino REAL que crea el
/// cargo automatico (a diferencia de otros tests de este modulo, que no siembran lineas y por eso ese camino
/// nunca corre).</para>
/// </summary>
public class Adr044T3bConfirmPenaltyTargetInvoiceTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr044-t3b-confirm-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record Harness(BookingCancellationService Service, AppDbContext Ctx, Mock<IInvoiceService> InvoiceMock);

    private static Harness BuildService()
    {
        var ctx = NewDbContext();
        var invoiceMock = new Mock<IInvoiceService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnableCancellationDebitNote = true,
                CancellationDebitNoteGraceDays = 15,
                CancellationDebitNoteHardWarnDays = 60,
                CancellationDebitNoteFourEyesThreshold = 2_000_000m,
            });

        invoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                var nd = new Invoice
                {
                    PublicId = Guid.NewGuid(),
                    TipoComprobante = 12,
                    Resultado = "A",
                    ImporteTotal = req.Items.Sum(i => i.Total),
                    MonId = req.MonId,
                    MonCotiz = req.MonCotiz,
                };
                ctx.Invoices.Add(nd);
                ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

        return new Harness(service, ctx, invoiceMock);
    }

    /// <summary>
    /// Siembra un BC POST-NC (penalidad todavia <c>Estimated</c>, lista para confirmar) con UNA linea
    /// pass-through del operador CON <c>RefundCap</c> &gt; 0 en ARS (para que el neteo/cargo automatico de
    /// <c>AllocateConfirmedPenaltyToLinesAsync</c> corra de verdad). El caller agrega, si el test lo necesita,
    /// una SEGUNDA factura activa con <see cref="AddSecondActiveInvoiceAsync"/>.
    /// </summary>
    private static async Task<(BookingCancellation Bc, Invoice Original, Reserva Reserva, Supplier Supplier)> SeedPreConfirmWithLineAsync(
        AppDbContext ctx, decimal refundCap = 30_000m)
    {
        var customer = new Customer { FullName = "Cliente T3b Confirm", IsActive = true };
        var supplier = new Supplier { Name = "Operador T3b Confirm", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T3BCONFIRM", Name = "Reserva T3b Confirm", PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 700, CAE = "cae-orig-confirm",
            Resultado = "A", MonId = "PES", ImporteTotal = 200_000m, ImporteNeto = 200_000m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 701, CAE = "cae-nc-confirm",
            Resultado = "A", ReservaId = reserva.Id,
        };
        ctx.Invoices.Add(original);
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();
        creditNote.OriginalInvoiceId = original.Id;
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cliente cancelo; penalidad a confirmar por el operador",
            DraftedByUserId = "vendedor-1", ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-5),
            ConfirmedByUserId = "vendedor-1",
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = "ARS",
                AgencyTaxConditionAtEvent = "MONOTRIBUTO",
                SupplierTaxConditionAtEvent = "MONOTRIBUTO",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                FetchedAt = DateTime.UtcNow.AddDays(-5),
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = "ARS",
            RefundCap = refundCap, PenaltyStatus = PenaltyStatus.Estimated,
        };
        ctx.BookingCancellationLines.Add(line);
        await ctx.SaveChangesAsync();

        return (bc, original, reserva, supplier);
    }

    private static async Task<Invoice> AddSecondActiveInvoiceAsync(
        AppDbContext ctx, Reserva reserva, string monId = "DOL", decimal monCotiz = 1000m, decimal importeTotal = 300m)
    {
        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 950 + (int)(DateTime.UtcNow.Ticks % 1000),
            CAE = "cae-second-confirm", Resultado = "A", MonId = monId, MonCotiz = monCotiz, ImporteTotal = importeTotal,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        return invoice;
    }

    private static ConfirmPenaltyRequest RequestWithTargetInvoice(Guid? targetInvoicePublicId, decimal amount = 20_000m)
        => new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: amount,
            OperatorConfirmationDate: DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose: null,
            SupportingDocumentReference: "mail-operador-multa.pdf",
            TargetInvoicePublicId: targetInvoicePublicId);

    // ============================================================
    // 1) Con UNA sola factura activa: TargetInvoicePublicId se ignora (autocompletado transparente).
    // ============================================================

    [Fact]
    public async Task SingleActiveInvoice_TargetInvoicePublicIdIgnored_AutoResolves()
    {
        var h = BuildService();
        var (bc, original, _, _) = await SeedPreConfirmWithLineAsync(h.Ctx);

        // Mandamos un GUID cualquiera (ni siquiera valido): con 1 sola factura activa, el autocompletado
        // resuelve SOLO y este campo directamente no se llega a mirar.
        var dto = await h.Service.ConfirmPenaltyAsync(
            bc.PublicId, RequestWithTargetInvoice(Guid.NewGuid()), "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        var charge = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, charge.TargetInvoiceId);
    }

    // ============================================================
    // 2) Con 2+ facturas activas y una eleccion VALIDA: el cargo automatico apunta a la elegida.
    // ============================================================

    [Fact]
    public async Task TwoActiveInvoices_ValidTargetInvoicePublicId_ChargeTargetsChosenInvoice()
    {
        var h = BuildService();
        var (bc, _, reserva, _) = await SeedPreConfirmWithLineAsync(h.Ctx);
        // Misma moneda (ARS/PES) que el cargo: aisla la eleccion de factura (Decision 1) de la conversion de
        // moneda (Decision 2, que exige ademas el flag EnableMultiCurrencyInvoicing y el TC estimado del cargo).
        var secondInvoice = await AddSecondActiveInvoiceAsync(h.Ctx, reserva, monId: "PES", monCotiz: 1m, importeTotal: 150_000m);

        var dto = await h.Service.ConfirmPenaltyAsync(
            bc.PublicId, RequestWithTargetInvoice(secondInvoice.PublicId), "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus); // el cargo SI quedo resuelto -> la ND emite (no manual).
        var charge = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().SingleAsync();
        Assert.Equal(secondInvoice.Id, charge.TargetInvoiceId);
    }

    // ============================================================
    // 3) Con 2+ facturas activas y una eleccion INVALIDA (no es factura activa de la reserva): rechaza y no
    //    persiste NADA (atomicidad — mismo criterio que el resto del modulo).
    // ============================================================

    [Fact]
    public async Task TwoActiveInvoices_InvalidTargetInvoicePublicId_RejectsAndMutatesNothing()
    {
        var h = BuildService();
        var (bc, _, reserva, _) = await SeedPreConfirmWithLineAsync(h.Ctx);
        await AddSecondActiveInvoiceAsync(h.Ctx, reserva);

        // Un GUID que no corresponde a NINGUNA factura de la reserva.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bc.PublicId, RequestWithTargetInvoice(Guid.NewGuid()), "cajero-1", "Cajero",
                requesterIsAdmin: false, ct: CancellationToken.None, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR044-TARGETINVOICE-001", ex.InvariantCode);

        // Nada se PERSISTIO: ni la penalidad quedo confirmada, ni se creo ningun cargo, ni se emitio ninguna ND.
        // AsNoTracking() para leer el estado REAL de la base (no el objeto tracked en memoria, que SI llego a
        // mutar in-process antes de que la validacion de la factura destino cortara la operacion completa —
        // el punto es que ese cambio nunca llego a un SaveChanges, mismo criterio de atomicidad que el resto
        // del modulo, ej. Adr044T3bTargetInvoiceAndTreasuryFxTests).
        var reloadedBc = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync();
        Assert.Equal(PenaltyStatus.Estimated, reloadedBc.PenaltyStatus);
        var reloadedLine = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(PenaltyStatus.Estimated, reloadedLine.PenaltyStatus);
        Assert.Equal(0, await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().CountAsync());
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 4) Con 2+ facturas activas y SIN eleccion: el cargo automatico queda sin factura destino (revision
    //    manual) — mismo comportamiento que antes de este campo (retrocompatibilidad).
    // ============================================================

    [Fact]
    public async Task TwoActiveInvoices_NoTargetInvoicePublicId_RoutesManual_SameAsBefore()
    {
        var h = BuildService();
        var (bc, _, reserva, _) = await SeedPreConfirmWithLineAsync(h.Ctx);
        await AddSecondActiveInvoiceAsync(h.Ctx, reserva);

        var dto = await h.Service.ConfirmPenaltyAsync(
            bc.PublicId, RequestWithTargetInvoice(targetInvoicePublicId: null), "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var charge = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().SingleAsync();
        Assert.Null(charge.TargetInvoiceId);
        // La penalidad SI quedo confirmada (el cargo se creo, solo la ND quedo pendiente de elegir factura).
        var reloadedBc = h.Ctx.BookingCancellations.AsNoTracking().Single();
        Assert.Equal(PenaltyStatus.Confirmed, reloadedBc.PenaltyStatus);
        Assert.Contains("factura", reloadedBc.DebitNoteArcaErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
