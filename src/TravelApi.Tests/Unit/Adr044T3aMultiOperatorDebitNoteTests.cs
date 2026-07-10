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
using TravelApi.Domain.Helpers;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 T3a (2026-07-10): tests UNIT de la ND multi-operador (reemplaza el candado "ARREGLO 2" por la
/// emision real con un renglon por cargo). Cubre: multi-operador misma moneda (1 ND, N renglones), cruce de
/// moneda entre un cargo y la factura (revision manual), 2+ facturas activas (revision manual), Responsable
/// Inscripto sin alicuota confirmada (bloqueada), fee de gestion (renglon aparte), absorber (sin renglon, con
/// rastro), y paridad byte-a-byte con el camino legacy (sin cargos / mono-operador con 1 cargo).
///
/// <para><b>Estrategia de siembra</b>: en vez de recorrer <c>ConfirmPenaltyAsync</c> (que dispara la emision
/// automaticamente al confirmar el operador PRINCIPAL, antes de que se puedan cargar cargos secundarios),
/// estos tests arman el BC YA CONFIRMADO a mano (mismos campos que <c>CaptureDebitNoteClassification</c>
/// dejaria) y usan <c>RetryDebitNoteEmissionAsync</c> (endpoint publico existente, "Reintentar" de la ficha)
/// para disparar <c>TryEmitCancellationDebitNoteAsync</c> con control total sobre que cargos existen en ese
/// momento. Mismo enfoque (InMemory + mocks, sin Docker) que el resto de la suite del modulo.</para>
/// </summary>
public class Adr044T3aMultiOperatorDebitNoteTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr044-t3a-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record Harness(BookingCancellationService Service, AppDbContext Ctx, Mock<IInvoiceService> InvoiceMock);

    private static Harness BuildService(OperationalFinanceSettings? settings = null)
    {
        var ctx = NewDbContext();
        var invoiceMock = new Mock<IInvoiceService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings ?? new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnableCancellationDebitNote = true,
                CancellationDebitNoteGraceDays = 15,
                CancellationDebitNoteHardWarnDays = 60,
                CancellationDebitNoteFourEyesThreshold = 2_000_000m,
            });

        // CreateAsync inserta una Invoice ND real en el ctx (para que la query de vinculacion por PublicId la
        // encuentre), y captura el request para que los tests inspeccionen sus Items.
        invoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
    /// Siembra un BC ya CONFIRMADO (pass-through, listo para reintentar la emision de la ND): reserva, factura
    /// original C=11 en pesos con CAE, NC total con CAE, y el BC en <c>AwaitingOperatorRefund</c> con todo el
    /// rastro de auditoria que el gating exige (clasificador + confirmador + finalidad). El operador PRINCIPAL
    /// es <paramref name="primarySupplier"/>. NO agrega lineas: cada test las agrega segun el escenario.
    /// </summary>
    private static async Task<(BookingCancellation Bc, Invoice Original, Reserva Reserva)> SeedConfirmedReadyToRetryAsync(
        AppDbContext ctx, Supplier primarySupplier, string agencyTaxCondition = "MONOTRIBUTISTA",
        decimal originalTotal = 500_000m)
    {
        var customer = new Customer { FullName = "Cliente T3a", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T3A", Name = "Reserva multi-operador T3a", PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 900, CAE = "cae-orig",
            Resultado = "A", MonId = "PES", ImporteTotal = originalTotal, ImporteNeto = originalTotal,
            ImporteIva = 0m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 901, CAE = "cae-nc",
            Resultado = "A", ReservaId = reserva.Id,
        };
        ctx.Invoices.Add(original);
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();
        creditNote.OriginalInvoiceId = original.Id;
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = primarySupplier.Id,
            OriginatingInvoiceId = original.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion multi-operador T3a",
            DraftedByUserId = "vendedor-1", ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-5),
            ConfirmedByUserId = "vendedor-1",
            // Estado "ya confirmado, listo para (re)emitir": mismo rastro que deja CaptureDebitNoteClassification.
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            PenaltyStatus = PenaltyStatus.Confirmed,
            ConceptClassifiedByUserId = "u1", ConceptClassifiedByUserName = "U1",
            ConceptClassifiedAt = DateTime.UtcNow.AddDays(-1),
            PenaltyConfirmedByUserId = "u1", PenaltyConfirmedByUserName = "U1",
            PenaltyConfirmedAt = DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge,
            PenaltyAmountAtEvent = 1m, // placeholder: el camino con cargos no lo usa; el legacy lo pisa si aplica.
            PenaltyCurrencyAtEvent = "ARS",
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = "ARS",
                AgencyTaxConditionAtEvent = agencyTaxCondition,
                SupplierTaxConditionAtEvent = "MONOTRIBUTISTA",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                FetchedAt = DateTime.UtcNow.AddDays(-5),
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc, original, reserva);
    }

    /// <summary>Agrega una linea CONFIRMADA con UN cargo (el "cargo base", tipicamente el automatico de Allocate).</summary>
    private static async Task<BookingCancellationLine> AddConfirmedLineWithChargeAsync(
        AppDbContext ctx, BookingCancellation bc, Supplier supplier, decimal amount, string currency = "ARS",
        ClientTransferMode transferMode = ClientTransferMode.AsIs, decimal? managementFeeAmount = null,
        OperatorChargeKind kind = OperatorChargeKind.AdministrativeFee)
    {
        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = ctx.BookingCancellationLines.Count() + 1,
            Scope = BookingCancellationLineScope.Full, Currency = currency,
            RefundCap = 0m, // ya neteado (no relevante para armar la ND, solo el eje cliente).
            PenaltyAmount = amount, RetainedDeductionAmount = amount,
            PenaltyStatus = PenaltyStatus.Confirmed,
        };
        ctx.BookingCancellationLines.Add(line);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = line.Id,
            Kind = kind,
            CollectionMode = PenaltyCollectionMode.Retenida,
            Amount = amount,
            Currency = currency,
            ClientTransferMode = transferMode,
            ManagementFeeAmount = managementFeeAmount,
            ConfirmedByUserId = "u1",
            ConfirmedByUserName = "U1",
            ConfirmedAt = DateTime.UtcNow.AddDays(-1),
        });
        await ctx.SaveChangesAsync();

        return line;
    }

    private static async Task<Supplier> AddSupplierAsync(AppDbContext ctx, string name)
    {
        var supplier = new Supplier { Name = name, IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier;
    }

    /// <summary>
    /// Siembra un BC en <c>AwaitingOperatorRefund</c> con la multa AUN Estimated (listo para el flujo de CONFIRM
    /// escalonado), con FiscalSnapshot Monotributo (asi la emision resuelve la alicuota 0% y SALE), y una linea
    /// por cada operador de <paramref name="suppliers"/> (el primero es el PRINCIPAL del BC). Sin cargos todavia:
    /// los crea AllocateConfirmedPenaltyToLinesAsync al confirmar.
    /// </summary>
    private static async Task<(BookingCancellation Bc, Reserva Reserva, List<BookingCancellationLine> Lines)>
        SeedEstimatedForConfirmFlowAsync(AppDbContext ctx, params Supplier[] suppliers)
    {
        var customer = new Customer { FullName = "Cliente T3a confirm", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T3A-CONF", Name = "Reserva confirm escalonado", PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 700, CAE = "cae-orig",
            Resultado = "A", MonId = "PES", ImporteTotal = 500_000m, ImporteNeto = 500_000m,
            ImporteIva = 0m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 701, CAE = "cae-nc",
            Resultado = "A", ReservaId = reserva.Id,
        };
        ctx.Invoices.Add(original);
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();
        creditNote.OriginalInvoiceId = original.Id;
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = suppliers[0].Id,
            OriginatingInvoiceId = original.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion multi-operador confirm escalonado",
            DraftedByUserId = "vendedor-1", ConfirmedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-5),
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = "ARS",
                AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent = "MONOTRIBUTISTA",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                FetchedAt = DateTime.UtcNow.AddDays(-5),
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var lines = new List<BookingCancellationLine>();
        foreach (var supplier in suppliers)
        {
            var line = new BookingCancellationLine
            {
                BookingCancellationId = bc.Id, SupplierId = supplier.Id,
                ServiceTable = CancellableServiceTable.Hotel, ServiceId = lines.Count + 1,
                Scope = BookingCancellationLineScope.Full, Currency = "ARS", RefundCap = 100_000m,
            };
            ctx.BookingCancellationLines.Add(line);
            lines.Add(line);
        }
        await ctx.SaveChangesAsync();

        return (bc, reserva, lines);
    }

    private static ConfirmPenaltyRequest ConfirmRequest(decimal amount, Guid supplierPublicId)
        => new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: amount,
            OperatorConfirmationDate: DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose: null,
            PenaltyCurrency: "ARS",
            SupportingDocumentReference: "https://docs/operador.pdf", // evita 4-eyes
            SupplierPublicId: supplierPublicId);

    // ============================================================
    // B1 (a) confirmacion escalonada: SECUNDARIO confirma PRIMERO -> al confirmar el PRINCIPAL sale 1 ND con 2 renglones
    // ============================================================

    [Fact]
    public async Task ConfirmFlow_SecondaryThenPrimary_EmitsOneDebitNoteWithBothCharges()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A"); // principal
        var supplierB = await AddSupplierAsync(h.Ctx, "Operador B"); // secundario
        var (bc, reserva, _) = await SeedEstimatedForConfirmFlowAsync(h.Ctx, supplierA, supplierB);

        // El SECUNDARIO (B) confirma PRIMERO: su cargo queda registrado, todavia sin emitir (el principal no confirmo).
        await h.Service.ConfirmPenaltyAsync(
            bc.PublicId, ConfirmRequest(10_000m, supplierB.PublicId), "userB", "Usuario B",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never); // aun no se emitio nada

        // El PRINCIPAL (A) confirma DESPUES: dispara la emision, que ve AMBOS cargos -> 1 ND con 2 renglones.
        await h.Service.ConfirmPenaltyAsync(
            bc.PublicId, ConfirmRequest(20_000m, supplierA.PublicId), "userA", "Usuario A",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);

        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r => r.Items.Count == 2 && r.Items.Sum(i => i.Total) == 30_000m),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(DebitNoteStatus.Pending, reloaded.DebitNoteStatus);

        // Ambos operadores comparten la ND emitida -> el read-model muestra a los DOS como "encolada" (no manual).
        var situations = await h.Service.GetOperatorPenaltySituationsAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, ct: default);
        Assert.Equal(2, situations.Count);
        Assert.All(situations, s => Assert.Equal(
            OperatorPenaltySituationState.DebitNoteQueued.ToString(), s.State));
    }

    // ============================================================
    // B1 (b) confirmacion escalonada: PRINCIPAL emite -> SECUNDARIO confirma DESPUES -> cargo huerfano visible (manual)
    // ============================================================

    [Fact]
    public async Task ConfirmFlow_PrimaryEmitsThenSecondaryConfirms_SecondaryShownAsComplementaryManual()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A"); // principal
        var supplierB = await AddSupplierAsync(h.Ctx, "Operador B"); // secundario
        var (bc, reserva, lines) = await SeedEstimatedForConfirmFlowAsync(h.Ctx, supplierA, supplierB);

        // El PRINCIPAL (A) confirma primero -> emite su ND (1 renglon, solo A).
        await h.Service.ConfirmPenaltyAsync(
            bc.PublicId, ConfirmRequest(20_000m, supplierA.PublicId), "userA", "Usuario A",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r => r.Items.Count == 1 && r.Items[0].Total == 20_000m),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        var afterPrimary = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.NotNull(afterPrimary.DebitNoteInvoiceId); // ND del principal ya en vuelo

        // El SECUNDARIO (B) confirma DESPUES: su cargo NO cabe en la ND ya emitida -> NO se emite otra ND, y su
        // linea queda marcada para resolucion manual (nota de debito complementaria), VISIBLE en la ficha.
        await h.Service.ConfirmPenaltyAsync(
            bc.PublicId, ConfirmRequest(10_000m, supplierB.PublicId), "userB", "Usuario B",
            requesterIsAdmin: false, ct: default, userCanClassifyAgencyPenalty: true);

        // NO hubo una segunda emision.
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // La linea del secundario quedo en ManualReview con el aviso claro (sin jerga).
        var lineB = await h.Ctx.BookingCancellationLines.AsNoTracking()
            .SingleAsync(l => l.BookingCancellationId == bc.Id && l.SupplierId == supplierB.Id);
        Assert.Equal(DebitNoteStatus.ManualReview, lineB.DebitNoteStatus);
        Assert.Contains("nota de débito complementaria", lineB.DebitNoteArcaErrorMessage);

        // El estado de la ND del BC padre (del PRINCIPAL) NO se toco: sigue en vuelo (Pending).
        var afterSecondary = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(DebitNoteStatus.Pending, afterSecondary.DebitNoteStatus);

        // Read-model: el PRINCIPAL muestra "encolada" (su ND salio bien), el SECUNDARIO "necesita revision manual".
        var situations = await h.Service.GetOperatorPenaltySituationsAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, ct: default);
        Assert.Equal(2, situations.Count);
        var primary = situations.Single(s => s.SupplierPublicId == supplierA.PublicId);
        var secondary = situations.Single(s => s.SupplierPublicId == supplierB.PublicId);
        Assert.Equal(OperatorPenaltySituationState.DebitNoteQueued.ToString(), primary.State);
        Assert.Equal(OperatorPenaltySituationState.MultiOperatorNeedsManualReview.ToString(), secondary.State);
    }

    // ============================================================
    // Menor 1 (caso negativo): un cargo Withholding NO admite cargo de gestion ni absorcion
    // ============================================================

    [Fact]
    public async Task AddOperatorCharge_WithholdingWithManagementFee_Rejected()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        // La multa del principal ya esta confirmada (el seed la deja Confirmed) con su cargo base.
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.AddOperatorChargeAsync(
                bc.PublicId,
                new AddOperatorChargeRequest(
                    Kind: OperatorChargeKind.Withholding,
                    CollectionMode: PenaltyCollectionMode.Retenida,
                    Amount: 3_000m,
                    Currency: "ARS",
                    ClientTransferMode: ClientTransferMode.WithManagementFee,
                    ManagementFeeAmount: 500m),
                userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true));
        Assert.Contains("retención fiscal no se le traslada al cliente", ex.Message);
    }

    // ============================================================
    // 1) Multi-operador, misma moneda -> 1 ND con 2 renglones
    // ============================================================

    [Fact]
    public async Task TwoOperators_SameCurrency_EmitsOneDebitNote_WithTwoLines()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var supplierB = await AddSupplierAsync(h.Ctx, "Operador B");
        var (bc, original, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);

        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m);
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierB, amount: 10_000m);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.NotNull(reloaded.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Pending, reloaded.DebitNoteStatus);
        // El snapshot fiscal del padre ahora refleja el TOTAL de los dos cargos, no solo el del principal.
        Assert.Equal(30_000m, reloaded.PenaltyAmountAtEvent);

        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r =>
                r.IsDebitNote &&
                r.Items.Count == 2 &&
                r.Items.Sum(i => i.Total) == 30_000m &&
                r.Items.All(i => i.AlicuotaIvaId == 3) &&
                // DATA-EXPOSURE (decidido por Gaston 2026-07-10): el comprobante del pasajero SÍ nombra al
                // mayorista — cada renglón nombra a su operador.
                r.Items.Any(i => i.Description.Contains("Penalidad de Operador A por cancelación")) &&
                r.Items.Any(i => i.Description.Contains("Penalidad de Operador B por cancelación"))),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 2) Cargo en otra moneda que la factura -> revision manual, mensaje claro
    // ============================================================

    [Fact]
    public async Task ChargeInDifferentCurrencyThanInvoice_RoutesManual_WithClearMessage()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var supplierB = await AddSupplierAsync(h.Ctx, "Operador B");
        var (bc, original, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA); // factura en PES

        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m, currency: "ARS");
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierB, amount: 100m, currency: "USD"); // cruce de moneda

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(DebitNoteStatus.ManualReview, reloaded.DebitNoteStatus);
        Assert.Null(reloaded.DebitNoteInvoiceId);
        Assert.Contains("moneda distinta", reloaded.DebitNoteArcaErrorMessage);
        // No se filtran codigos crudos (data-exposure): ni "USD"/"DOL" ni "PES" en el mensaje al usuario.
        Assert.DoesNotContain("USD", reloaded.DebitNoteArcaErrorMessage);
        Assert.DoesNotContain("DOL", reloaded.DebitNoteArcaErrorMessage);

        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 3) 2+ facturas activas -> revision manual
    // ============================================================

    [Fact]
    public async Task TwoActiveInvoices_RoutesManual()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, original, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m);

        // Segunda factura de venta viva de la MISMA reserva (caso legitimo multimoneda, ADR-042).
        var secondInvoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 950, CAE = "cae-second",
            Resultado = "A", MonId = "DOL", MonCotiz = 1000m, ImporteTotal = 300m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        h.Ctx.Invoices.Add(secondInvoice);
        await h.Ctx.SaveChangesAsync();

        h.Ctx.BookingCancellationCreditNotes.AddRange(
            new BookingCancellationCreditNote { BookingCancellationId = bc.Id, OriginatingInvoiceId = original.Id },
            new BookingCancellationCreditNote { BookingCancellationId = bc.Id, OriginatingInvoiceId = secondInvoice.Id });
        await h.Ctx.SaveChangesAsync();

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Contains("más de una factura", reloaded.DebitNoteArcaErrorMessage);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 4) Responsable Inscripto sin alicuota confirmada -> bloqueada
    // ============================================================

    [Fact]
    public void ResolvePassThroughAlicuotaIvaId_ResponsableInscripto_WithoutSetting_ReturnsNull()
    {
        // Pure: Mono/Exento siempre 3; RI depende del parametro (sin firma contable, default null -> bloquea).
        Assert.Equal(3, BookingCancellationService.ResolvePassThroughAlicuotaIvaIdOrNull(
            TaxConditionCanonical.Monotributista, riPassThroughAlicuotaIvaIdSetting: null));
        Assert.Equal(3, BookingCancellationService.ResolvePassThroughAlicuotaIvaIdOrNull(
            TaxConditionCanonical.Exento, riPassThroughAlicuotaIvaIdSetting: null));
        Assert.Null(BookingCancellationService.ResolvePassThroughAlicuotaIvaIdOrNull(
            TaxConditionCanonical.ResponsableInscripto, riPassThroughAlicuotaIvaIdSetting: null));
        // Con el valor confirmado por el contador cargado en el panel, SI se puede automatizar.
        Assert.Equal(5, BookingCancellationService.ResolvePassThroughAlicuotaIvaIdOrNull(
            TaxConditionCanonical.ResponsableInscripto, riPassThroughAlicuotaIvaIdSetting: 5));
        Assert.Null(BookingCancellationService.ResolvePassThroughAlicuotaIvaIdOrNull(
            TaxConditionCanonical.ConsumidorFinal, riPassThroughAlicuotaIvaIdSetting: 5));
    }

    [Fact]
    public async Task ResponsableInscripto_WithoutConfirmedAlicuota_RoutesManual_WithClearMessage()
    {
        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnableCancellationDebitNote = true,
            CancellationDebitNoteGraceDays = 15,
            CancellationDebitNoteHardWarnDays = 60,
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
            CancellationDebitNoteRiPassThroughAlicuotaIvaId = null, // sin firma contable (default del proyecto).
        };
        var h = BuildService(settings);
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        // Emisor RI (agencia paso de Monotributo a Responsable Inscripto DESPUES de emitir esta factura C
        // historica — la letra del comprobante ya emitido no cambia, pero la condicion vigente al confirmar
        // la cancelacion SI puede ser distinta; ver ADR-044 T3 spec, punto 5).
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA, agencyTaxCondition: "RESPONSABLE_INSCRIPTO");
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Contains("alícuota de IVA", reloaded.DebitNoteArcaErrorMessage);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResponsableInscripto_WithConfirmedAlicuota_Emits()
    {
        // Una vez que el contador confirma el valor y el admin lo carga en el panel, la ND SI se automatiza.
        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnableCancellationDebitNote = true,
            CancellationDebitNoteGraceDays = 15,
            CancellationDebitNoteHardWarnDays = 60,
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
            CancellationDebitNoteRiPassThroughAlicuotaIvaId = 5, // 21%, confirmado.
        };
        var h = BuildService(settings);
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA, agencyTaxCondition: "RESPONSABLE_INSCRIPTO");
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r => r.Items.Count == 1 && r.Items[0].AlicuotaIvaId == 5),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 5) Fee de gestion -> renglon aparte
    // ============================================================

    [Fact]
    public async Task ManagementFeeCharge_AddsSeparateLine()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierA, amount: 20_000m,
            transferMode: ClientTransferMode.WithManagementFee, managementFeeAmount: 3_000m);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r =>
                r.Items.Count == 2 &&
                r.Items.Sum(i => i.Total) == 23_000m &&
                r.Items.Any(i => i.Total == 20_000m) &&
                r.Items.Any(i => i.Total == 3_000m && i.Description.Contains("gestión"))),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 6) Absorber -> sin renglon, con rastro (auditoria)
    // ============================================================

    [Fact]
    public async Task AbsorbedCharge_ProducesNoLine_ButKeepsAuditTrail()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var supplierB = await AddSupplierAsync(h.Ctx, "Operador B");
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        // A se traslada tal cual; B se absorbe (la agencia decide no cobrarselo al cliente).
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m);
        var lineB = await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierB, amount: 5_000m, transferMode: ClientTransferMode.Absorbed);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        // Solo el renglon de A: el cargo absorbido de B no aparece en la ND.
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r => r.Items.Count == 1 && r.Items[0].Total == 20_000m),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // El rastro de auditoria queda en el propio cargo persistido (ClientTransferMode=Absorbed), no se borra.
        var chargeB = await h.Ctx.BookingCancellationLineOperatorCharges
            .AsNoTracking().SingleAsync(c => c.BookingCancellationLineId == lineB.Id);
        Assert.Equal(ClientTransferMode.Absorbed, chargeB.ClientTransferMode);
        Assert.Equal(5_000m, chargeB.Amount);
    }

    // ============================================================
    // 7) Paridad legacy: BC sin cargos (confirmado antes de ADR-044 T2) sigue emitiendo por el camino viejo
    // ============================================================

    [Fact]
    public async Task LegacyBcWithoutCharges_EmitsSingleItem_ByteIdenticalToBefore()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, original, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        bc.PenaltyAmountAtEvent = 45_000m; // el monto real de la multa legacy vive SOLO en el snapshot del padre.
        await h.Ctx.SaveChangesAsync();
        // Sin lineas ni cargos: BC anterior a ADR-044 T2 (o cap ya en 0 al confirmar).

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r =>
                r.Items.Count == 1 &&
                r.Items[0].UnitPrice == 45_000m &&
                r.Items[0].AlicuotaIvaId == 3 &&
                r.Items[0].Description.StartsWith("Penalidad por cancelacion s/Fc") &&
                !r.Items[0].Description.Contains("Operador")), // sin nombre de operador: formato legacy intacto.
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 8) Paridad mono-operador con 1 cargo (post ADR-044 T2, sin fee ni absorcion)
    // ============================================================

    [Fact]
    public async Task SingleOperator_OneCharge_EmitsSameAmountAndAlicuota_AsLegacyPath()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 45_000m);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        // Mismo monto y misma alicuota (0%, Monotributo) que el camino legacy de la Prueba 7. La Description del
        // renglon pass-through SÍ nombra al mayorista (decidido por Gaston 2026-07-10).
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r =>
                r.Items.Count == 1 &&
                r.Items[0].UnitPrice == 45_000m &&
                r.Items[0].Total == 45_000m &&
                r.Items[0].AlicuotaIvaId == 3 &&
                r.Items[0].Description.Contains("Penalidad de Operador A por cancelación")),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 9) B2 (guarda de sobre-facturacion): 2 cargos que sumados superan el total de la factura -> Manual
    // ============================================================

    [Fact]
    public async Task ChargesTotalExceedsInvoiceTotal_RoutesManual()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var supplierB = await AddSupplierAsync(h.Ctx, "Operador B");
        // Factura original de 500.000; dos cargos que sumados (300.000 + 250.000 = 550.000) la superan.
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA, originalTotal: 500_000m);
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 300_000m);
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierB, amount: 250_000m);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Contains("supera el total de la factura", reloaded.DebitNoteArcaErrorMessage);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ChargesTotalWithManagementFeeExactlyAtInvoiceTotal_StillEmits_RoundingBoundary()
    {
        // Caso borde: cargo (400.000) + cargo de gestion (100.000) = 500.000, EXACTO al total de la factura.
        // El guard es "> total", asi que el borde exacto NO bloquea: emite con los 2 renglones.
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA, originalTotal: 500_000m);
        await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierA, amount: 400_000m,
            transferMode: ClientTransferMode.WithManagementFee, managementFeeAmount: 100_000m);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r => r.Items.Count == 2 && r.Items.Sum(i => i.Total) == 500_000m),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 10) Menor 1: filtro Withholding a nivel del motor de la ND
    // ============================================================

    [Fact]
    public async Task WithholdingCharge_MixedWithPassThrough_ExcludedFromDebitNoteLines()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        // Un cargo pass-through (se traslada) + una retencion fiscal (Withholding: NUNCA llega al cliente).
        var line = await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m);
        h.Ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = line.Id,
            Kind = OperatorChargeKind.Withholding,
            CollectionMode = PenaltyCollectionMode.Retenida,
            Amount = 3_000m,
            Currency = "ARS",
            ClientTransferMode = ClientTransferMode.AsIs,
            ConfirmedByUserId = "u1",
            ConfirmedAt = DateTime.UtcNow.AddDays(-1),
        });
        await h.Ctx.SaveChangesAsync();

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        // La ND tiene SOLO el renglon pass-through (20.000). El Withholding (3.000) NO aparece: es credito fiscal
        // de la agencia, no se le cobra al cliente.
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r => r.Items.Count == 1 && r.Items[0].Total == 20_000m),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
