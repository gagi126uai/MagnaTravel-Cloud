using System;
using System.Collections.Generic;
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
/// Regla fiscal cerrada (firmada): anular una reserva con multa = NC TOTAL + Nota de Debito
/// por la PENALIDAD, donde la penalidad es PASS-THROUGH del operador (plata del operador, NO
/// ingreso gravado de la agencia; la agencia replica la cadena del operador y le cobra la multa
/// al cliente con una ND no gravada).
///
/// <para>Estos tests FIJAN el cambio de modelo: antes (ADR-013 original) el concepto
/// pass-through NO emitia ND. Ahora SI. Cubren:</para>
/// <list type="bullet">
///   <item>(b1) Pure: ConceptEmitsDebitNote cubre pass-through ademas de agency-owned.</item>
///   <item>(b2) Pure: el gating (EvaluateDebitNoteGating) deja pasar pass-through cuando todo
///         lo demas cuadra (confirmada, factura C, ARS, sin tributos, monto valido, auditoria).</item>
///   <item>(b3) End-to-end: ConfirmPenaltyAsync con pass-through emite la ND (vincula
///         DebitNoteInvoiceId, deja DebitNoteStatus=Pending).</item>
///   <item>(c) "Sin multa" = NC total sin ND (con la penalidad no confirmada, el gating no emite).</item>
///   <item>Anti-doble-cobro: con la ND pass-through en juego, NO se puede ademas netear la
///         penalidad del refund (INV-ADR013-001), porque seria cobrar la misma multa dos veces.</item>
/// </list>
///
/// <para>Tests UNIT con EF InMemory (sin Docker), mismo trade-off que
/// <see cref="BookingCancellationServicePartialCreditNoteTests"/>: InMemory NO valida CHECK
/// constraints SQL ni xmin. Cubrimos la LOGICA del gating y la emision (vinculacion de la ND).</para>
/// </summary>
public class PassThroughPenaltyDebitNoteTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"passthrough-nd-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Builder del service. <paramref name="debitNoteOn"/> prende EnableCancellationDebitNote (la
    /// emision de la ND vive detras de ese flag existente; NO es un flag nuevo). El InvoiceMock
    /// inserta una Invoice real en el ctx al "crear" la ND, asi la query de vinculacion la resuelve.
    /// </summary>
    private static (BookingCancellationService Service, Mock<IInvoiceService> InvoiceMock) BuildService(
        AppDbContext ctx, bool debitNoteOn = true, decimal fourEyesThreshold = 1_000_000m)
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
                OperatorRefundTimeoutDays = 60,
                CancellationDebitNoteFourEyesThreshold = fourEyesThreshold,
                CancellationDebitNoteGraceDays = 30,
                CancellationDebitNoteHardWarnDays = 90,
            });

        // Al "emitir" la ND, insertamos la Invoice en el ctx con un PublicId conocido para que
        // la query de vinculacion (Where PublicId) la encuentre y vincule DebitNoteInvoiceId.
        invoiceMock
            .Setup(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                var nd = new Invoice
                {
                    PublicId = Guid.NewGuid(),
                    TipoComprobante = 12, // ND C
                    Resultado = "A",
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

        return (service, invoiceMock);
    }

    /// <summary>
    /// Siembra un BC POST-NC (AwaitingOperatorRefund + CreditNoteInvoiceId seteado), con factura
    /// original C en ARS sin tributos. Es la precondicion del confirm-penalty diferido. El
    /// concepto arranca en pass-through (default) — el caso de la regla cerrada.
    /// </summary>
    private static async Task<(BookingCancellation Bc, Invoice OriginatingInvoice, Supplier Supplier)> SeedPostNcBcAsync(
        AppDbContext ctx, PenaltyOwnership supplierOwnership = PenaltyOwnership.Operator, int originalTipo = 11)
    {
        var customer = new Customer { FullName = "Cliente PT", IsActive = true };
        var supplier = new Supplier { Name = "Operador PT", IsActive = true, PenaltyOwnership = supplierOwnership };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-PT", Name = "Reserva PT", PayerId = customer.Id, Status = EstadoReserva.PendingOperatorRefund };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var originating = new Invoice
        {
            TipoComprobante = originalTipo, // 11 = Factura C
            PuntoDeVenta = 1, NumeroComprobante = 500, CAE = "111", Resultado = "A",
            ImporteTotal = 200_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(originating);
        await ctx.SaveChangesAsync();

        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 501, CAE = "222", Resultado = "A",
            ImporteTotal = 200_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = originating.Id,
            CreditNoteInvoiceId = creditNote.Id, // NC con CAE ya emitida
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Anulacion con multa pass-through",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-2),
            ConfirmedByUserId = "vendedor-1",
            AmountPaidAtCancellation = 200_000m,
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = "ARS",
                AgencyTaxConditionAtEvent = "MONOTRIBUTO",
                SupplierTaxConditionAtEvent = "MONOTRIBUTO",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                FetchedAt = DateTime.UtcNow.AddDays(-2),
            },
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough, // default pass-through
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc, originating, supplier);
    }

    // ============================================================
    // (b1) Pure: ConceptEmitsDebitNote cubre pass-through
    // ============================================================

    [Fact]
    public void ConceptEmitsDebitNote_PassThrough_True_ButNotAgencyOwned()
    {
        // La regla cerrada: pass-through EMITE ND, pero NO es ingreso propio (gravado) de la agencia.
        Assert.True(BookingCancellationService.ConceptEmitsDebitNote(CancellationConceptKind.OperatorPenaltyPassThrough));
        Assert.False(BookingCancellationService.ConceptIsAgencyOwnedDebitNote(CancellationConceptKind.OperatorPenaltyPassThrough));

        // Los cargos propios de la agencia siguen emitiendo ND y siendo gravados.
        Assert.True(BookingCancellationService.ConceptEmitsDebitNote(CancellationConceptKind.AgencyManagementFee));
        Assert.True(BookingCancellationService.ConceptEmitsDebitNote(CancellationConceptKind.AgencyCancellationFee));

        // Los seguros NO emiten ND automatica (revision manual).
        Assert.False(BookingCancellationService.ConceptEmitsDebitNote(CancellationConceptKind.RealInsurancePremium));
        Assert.False(BookingCancellationService.ConceptEmitsDebitNote(CancellationConceptKind.AgencyCancellationCoverage));
        Assert.False(BookingCancellationService.ConceptEmitsDebitNote(CancellationConceptKind.AgencyInsuranceCommission));
    }

    // ============================================================
    // (b2) Pure: el gating deja pasar pass-through cuando todo cuadra
    // ============================================================

    [Fact]
    public void Gating_PassThrough_AllConditionsMet_Emits()
    {
        var originating = new Invoice { TipoComprobante = 11, MonId = "PES", ImporteTotal = 200_000m };
        var bc = new BookingCancellation
        {
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            PenaltyStatus = PenaltyStatus.Confirmed,
            ConceptClassifiedByUserId = "u1",
            PenaltyConfirmedByUserId = "u1",
            DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge,
            PenaltyAmountAtEvent = 30_000m,
            // Operador retiene la penalidad (pass-through) y AUN ASI emite ND segun la regla cerrada.
            Supplier = new Supplier { PenaltyOwnership = PenaltyOwnership.Operator },
        };

        var reason = BookingCancellationService.EvaluateDebitNoteGating(bc, originating);
        Assert.Null(reason); // pasa el gating -> emite ND
    }

    [Fact]
    public void Gating_PassThrough_PenaltyNotConfirmed_RoutesManual_NoND()
    {
        // "Sin multa confirmada" = NC total SIN ND: el gating no emite sobre una penalidad Estimated.
        var originating = new Invoice { TipoComprobante = 11, MonId = "PES", ImporteTotal = 200_000m };
        var bc = new BookingCancellation
        {
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            PenaltyStatus = PenaltyStatus.Estimated, // todavia no confirmo el operador
            ConceptClassifiedByUserId = "u1",
            PenaltyConfirmedByUserId = "u1",
            DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge,
            PenaltyAmountAtEvent = 30_000m,
            Supplier = new Supplier { PenaltyOwnership = PenaltyOwnership.Operator },
        };

        var reason = BookingCancellationService.EvaluateDebitNoteGating(bc, originating);
        Assert.NotNull(reason); // no emite ND -> queda NC total sola
    }

    [Fact]
    public void Gating_Insurance_RoutesManual()
    {
        // Un seguro NO emite ND automatica aunque todo lo demas cuadre.
        var originating = new Invoice { TipoComprobante = 11, MonId = "PES", ImporteTotal = 200_000m };
        var bc = new BookingCancellation
        {
            ConceptKind = CancellationConceptKind.RealInsurancePremium,
            PenaltyStatus = PenaltyStatus.Confirmed,
            ConceptClassifiedByUserId = "u1",
            PenaltyConfirmedByUserId = "u1",
            DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge,
            PenaltyAmountAtEvent = 30_000m,
            Supplier = new Supplier { PenaltyOwnership = PenaltyOwnership.Agency },
        };

        var reason = BookingCancellationService.EvaluateDebitNoteGating(bc, originating);
        Assert.NotNull(reason);
    }

    // ============================================================
    // (b3) End-to-end: ConfirmPenaltyAsync con pass-through emite la ND
    // ============================================================

    [Fact]
    public async Task ConfirmPenalty_PassThrough_EmitsDebitNote_LinksInvoice()
    {
        using var ctx = NewDbContext();
        var (bc, _, _) = await SeedPostNcBcAsync(ctx);
        var (service, invoiceMock) = BuildService(ctx, debitNoteOn: true);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 30_000m,
            OperatorConfirmationDate: DateTime.UtcNow.Date,
            DebitNotePurpose: null,
            SupportingDocumentReference: "mail-operador-multa.pdf"); // con soporte -> no exige 4-eyes

        var dto = await service.ConfirmPenaltyAsync(
            bc.PublicId, request, "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);

        // La ND pass-through se emitio: quedo vinculada y en Pending (la NC total ya estaba emitida).
        Assert.NotNull(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Pending, reloaded.DebitNoteStatus);
        Assert.Equal(PenaltyStatus.Confirmed, reloaded.PenaltyStatus);
        Assert.Equal(30_000m, reloaded.PenaltyAmountAtEvent);
        Assert.Equal(CancellationConceptKind.OperatorPenaltyPassThrough, reloaded.ConceptKind);

        // El item de la ND se arma como NO gravado (AlicuotaIvaId=3 / 0%) y la ND es por el monto
        // de la multa: la agencia NO declara ingreso gravado por una penalidad pass-through.
        invoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r =>
                r.IsDebitNote &&
                r.Items.Count == 1 &&
                r.Items[0].UnitPrice == 30_000m &&
                r.Items[0].AlicuotaIvaId == 3),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Equal("Pending", dto.DebitNoteStatus);
    }

    [Fact]
    public async Task ConfirmPenalty_FlagOff_DoesNotEmit_Inert()
    {
        // Con EnableCancellationDebitNote OFF el endpoint es inerte (byte-identidad con antes de ADR-014):
        // NO emite ND, rechaza con InvalidOperationException sin mutar nada.
        using var ctx = NewDbContext();
        var (bc, _, _) = await SeedPostNcBcAsync(ctx);
        var (service, invoiceMock) = BuildService(ctx, debitNoteOn: false);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 30_000m,
            OperatorConfirmationDate: DateTime.UtcNow.Date,
            SupportingDocumentReference: "mail.pdf");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmPenaltyAsync(
                bc.PublicId, request, "cajero-1", "Cajero",
                requesterIsAdmin: false, ct: CancellationToken.None,
                userCanClassifyAgencyPenalty: true));

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Null(reloaded.DebitNoteInvoiceId);
        invoiceMock.Verify(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmPenalty_PassThrough_OperatorAmount_GoesToManual_WhenInvoiceNotC()
    {
        // Aunque pass-through ahora emite ND, los OTROS guards conservadores siguen vivos: una factura
        // original que NO es C (ej. Factura B = 6) rutea a revision manual (no se auto-emite). Esto FIJA
        // que solo levantamos el bloqueo de pass-through, no los demas controles fiscales.
        using var ctx = NewDbContext();
        var (bc, _, _) = await SeedPostNcBcAsync(ctx, originalTipo: 6); // Factura B
        var (service, invoiceMock) = BuildService(ctx, debitNoteOn: true);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 30_000m,
            OperatorConfirmationDate: DateTime.UtcNow.Date,
            SupportingDocumentReference: "mail.pdf");

        await service.ConfirmPenaltyAsync(
            bc.PublicId, request, "cajero-1", "Cajero",
            requesterIsAdmin: false, ct: CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        var reloaded = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        // No emitio: ruteo a revision manual (factura B). No hay ND vinculada.
        Assert.Null(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.ManualReview, reloaded.DebitNoteStatus);
        invoiceMock.Verify(s => s.CreateAsync(It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
