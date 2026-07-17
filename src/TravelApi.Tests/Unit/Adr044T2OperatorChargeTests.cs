using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 T2 Addendum (2026-07-10): tests UNIT del testing obligatorio antes de mergear T2 (los 9 casos
/// listados en el ADR, incluidos los 3 "sitios de plata" nombrados: <c>AllocateConfirmedPenaltyToLinesAsync</c>,
/// <c>ReverseConfirmedPenaltyFromLinesAsync</c>, <c>SupplierCancellationCircuitReader</c> y
/// <c>OperatorRefundReadModelService</c>). Mismo enfoque que <see cref="CancellationWaivePenaltyTests"/>: DbContext
/// InMemory + mocks, sin Docker.
/// </summary>
public class Adr044T2OperatorChargeTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr044-t2-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private sealed record Harness(BookingCancellationService Service, AppDbContext Ctx, Mock<IAuditService> AuditMock);

    private static Harness BuildService(bool flagOn = true)
    {
        var ctx = NewDbContext();
        var invoiceMock = new Mock<IInvoiceService>();
        var auditMock = new Mock<IAuditService>();
        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnableCancellationDebitNote = flagOn,
            EnableMultiCurrencyInvoicing = true,
            CancellationDebitNoteGraceDays = 15,
            CancellationDebitNoteHardWarnDays = 60,
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
        };
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            new Mock<IApprovalRequestService>().Object,
            auditMock.Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

        return new Harness(service, ctx, auditMock);
    }

    /// <summary>
    /// Semilla minima: reserva + operador + BC con UNA linea con RefundCap = <paramref name="refundCap"/>
    /// (capBeforePenalty, la multa todavia no se neteo). PenaltyStatus=Estimated. Devuelve el BC (tracked) y la
    /// linea (tracked) para que cada test los lleve a su estado.
    /// </summary>
    private static async Task<(BookingCancellation Bc, BookingCancellationLine Line, Supplier Supplier)> SeedBcWithLineAsync(
        AppDbContext ctx,
        decimal refundCap = 1000m,
        string currency = "ARS",
        SupplierInvoicingMode invoicingMode = SupplierInvoicingMode.TotalToCustomer)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador X", IsActive = true, InvoicingMode = invoicingMode };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T2",
            Name = "Reserva Test",
            PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund,
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // OriginatingInvoiceId es NOT NULL en BookingCancellation (FK requerida): sin una factura real, el
        // Include(b => b.OriginatingInvoice) que hace MapToDtoAsync deja la fila afuera (INNER JOIN contra
        // Id=0) y el mapeo final devuelve null. Semilla completa, igual que el resto de los tests del modulo.
        var original = new Invoice
        {
            TipoComprobante = 11,
            PuntoDeVenta = 1,
            NumeroComprobante = 100,
            CAE = "12345678",
            Resultado = "A",
            MonId = "PES",
            ImporteTotal = 100_000m,
            ImporteNeto = 100_000m,
            ImporteIva = 0m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        var creditNote = new Invoice
        {
            TipoComprobante = 13,
            PuntoDeVenta = 1,
            NumeroComprobante = 101,
            CAE = "99999999",
            Resultado = "A",
            ReservaId = reserva.Id,
            OriginalInvoiceId = original.Id,
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
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cliente anulo",
            DraftedByUserId = "vendedor-1",
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-10),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = supplier.Id,
            Currency = currency,
            RefundCap = refundCap,
            // SupplierInvoicingModeAtEvent queda NULL a proposito en la mayoria de los tests (parity legacy,
            // caso 9): el gate cae al fallback vivo de Supplier.InvoicingMode.
        };
        ctx.BookingCancellationLines.Add(line);
        await ctx.SaveChangesAsync();

        return (bc, line, supplier);
    }

    // ============================================================
    // Caso 1 — Fee + Withholding en la misma linea.
    // ============================================================

    [Fact]
    public async Task Allocate_CreatesFeeCharge_RefundCap_And_RetainedDeductionAmount_Coinciden()
    {
        var h = BuildService();
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        await h.Ctx.SaveChangesAsync(); // Allocate NO hace su propio SaveChanges (lo hace el caller).

        var line = h.Ctx.BookingCancellationLines.Single();
        Assert.Equal(700m, line.RefundCap);
        Assert.Equal(300m, line.RetainedDeductionAmount);
        Assert.Equal(300m, line.PenaltyAmount);
        Assert.Equal(PenaltyStatus.Confirmed, line.PenaltyStatus);

        var charges = h.Ctx.BookingCancellationLineOperatorCharges.ToList();
        var charge = Assert.Single(charges);
        Assert.Equal(OperatorChargeKind.AdministrativeFee, charge.Kind);
        Assert.Equal(PenaltyCollectionMode.Retenida, charge.CollectionMode);
        Assert.Equal(300m, charge.Amount);
        Assert.Equal("u", charge.ConfirmedByUserId);
    }

    [Fact]
    public async Task Withholding_DoesNotReduceRefundCap_NorClientCredit()
    {
        var h = BuildService();
        var (bc, line, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);

        // Cargo base automatico (Fee, Retenida).
        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        // El BC-padre queda Confirmed (lo hace normalmente CaptureDebitNoteClassification dentro de ConfirmPenaltyAsync).
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        // Cargo SECUNDARIO: retencion fiscal (Withholding), Retenida.
        var dto = await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(
                Kind: OperatorChargeKind.Withholding,
                CollectionMode: PenaltyCollectionMode.Retenida,
                Amount: 50m,
                Currency: "ARS"),
            userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true);
        Assert.NotNull(dto);

        var refreshedLine = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        // RefundCap y RetainedDeductionAmount NO se tocan: Withholding es credito fiscal, no perdida real.
        Assert.Equal(700m, refreshedLine.RefundCap);
        Assert.Equal(300m, refreshedLine.RetainedDeductionAmount);
        // PenaltyAmount (eje CLIENTE) tampoco suma el Withholding: nunca llega al cliente.
        Assert.Equal(300m, refreshedLine.PenaltyAmount);

        var charges = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().ToListAsync();
        Assert.Equal(2, charges.Count);
        Assert.Contains(charges, c => c.Kind == OperatorChargeKind.Withholding && c.Amount == 50m);

        // Invariante B1, con la mezcla Retenida(Fee) + Retenida(Withholding).
        Assert.Equal(1000m, refreshedLine.RefundCap + refreshedLine.RetainedDeductionAmount);
    }

    // ============================================================
    // Caso 2 — Fee (Retenida) + otro cargo FacturadaAparte.
    // ============================================================

    [Fact]
    public async Task FacturadaAparte_DoesNotReduceRefundCap_GeneratesDebtWithDocumentRef()
    {
        var h = BuildService();
        var (bc, line, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(
                Kind: OperatorChargeKind.Tax,
                CollectionMode: PenaltyCollectionMode.FacturadaAparte,
                Amount: 80m,
                Currency: "ARS",
                DocumentRef: "FACT-OP-123"),
            userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true);

        var refreshedLine = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        // FacturadaAparte NUNCA resta el RefundCap: el operador devuelve el bruto, se cobra por AP.
        Assert.Equal(700m, refreshedLine.RefundCap);
        Assert.Equal(300m, refreshedLine.RetainedDeductionAmount);
        // Tax (Kind != Withholding) SI suma al eje CLIENTE, sin importar la forma de cobro.
        Assert.Equal(380m, refreshedLine.PenaltyAmount);

        var facturada = await h.Ctx.BookingCancellationLineOperatorCharges
            .AsNoTracking().SingleAsync(c => c.CollectionMode == PenaltyCollectionMode.FacturadaAparte);
        Assert.Equal("FACT-OP-123", facturada.DocumentRef);
        Assert.Equal(80m, facturada.Amount);

        // Invariante B1 con la mezcla Retenida + FacturadaAparte.
        Assert.Equal(1000m, refreshedLine.RefundCap + refreshedLine.RetainedDeductionAmount);
    }

    [Fact]
    public async Task AddOperatorCharge_Requires_DocumentRef_When_FacturadaAparte()
    {
        var h = BuildService();
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);
        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(
                Kind: OperatorChargeKind.Tax,
                CollectionMode: PenaltyCollectionMode.FacturadaAparte,
                Amount: 80m,
                Currency: "ARS",
                DocumentRef: null),
            userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true));
    }

    /// <summary>
    /// Candado del contador (Flag 2, tanda de endurecimientos ADR-048 T2, 2026-07-17): una retencion fiscal
    /// (Withholding, credito fiscal de la agencia) NUNCA puede cargarse como "facturada aparte" (deuda NUEVA
    /// que el operador le exige a la agencia). Si se permitiera, el lector compartido de deuda "facturada
    /// aparte" (que filtra solo por CollectionMode, no por Kind) sumaria una retencion como si fuera deuda real
    /// al operador. Hoy la UI no ofrece esta combinacion; este test fija que tampoco es alcanzable por API.
    /// </summary>
    [Fact]
    public async Task AddOperatorCharge_Rejects_Withholding_With_FacturadaAparte()
    {
        var h = BuildService();
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);
        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(
                Kind: OperatorChargeKind.Withholding,
                CollectionMode: PenaltyCollectionMode.FacturadaAparte,
                Amount: 80m,
                Currency: "ARS",
                DocumentRef: "FACT-OP-999"),
            userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true));

        // No se creo NINGUN cargo nuevo: el rechazo es ANTES de escribir (transaccion nunca arranca).
        Assert.Single(await h.Ctx.BookingCancellationLineOperatorCharges.ToListAsync());
        Assert.Contains("crédito fiscal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // Caso 3/4 — Reverse con cargos mixtos: restaura EXACTO RetainedDeductionAmount, nunca Withholding/FacturadaAparte.
    // ============================================================

    [Fact]
    public async Task Reverse_RestoresOnlyRetainedDeductionAmount_AndDeletesAllCharges()
    {
        var h = BuildService();
        var (bc, line, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Withholding, PenaltyCollectionMode.Retenida, 50m, "ARS"),
            "u", "U", default, userCanClassifyAgencyPenalty: true);
        await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Tax, PenaltyCollectionMode.FacturadaAparte, 80m, "ARS", DocumentRef: "F-1"),
            "u", "U", default, userCanClassifyAgencyPenalty: true);

        var beforeReverse = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(700m, beforeReverse.RefundCap);
        Assert.Equal(300m, beforeReverse.RetainedDeductionAmount);

        var restored = await h.Service.ReverseConfirmedPenaltyFromLinesAsync(bc, default);
        await h.Ctx.SaveChangesAsync();

        // El cap vuelve EXACTO a su valor previo (1000): solo se restauro RetainedDeductionAmount (300), NUNCA
        // Withholding (50) ni FacturadaAparte (80), que jamas salieron del cap.
        var afterLine = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(1000m, afterLine.RefundCap);
        Assert.Equal(0m, afterLine.RetainedDeductionAmount);
        Assert.Null(afterLine.PenaltyAmount);
        Assert.Equal(PenaltyStatus.Estimated, afterLine.PenaltyStatus);

        var restoredEntry = Assert.Single(restored);
        Assert.Equal(300m, restoredEntry.RestoredPenalty);
        Assert.Equal(700m, restoredEntry.OldRefundCap);
        Assert.Equal(1000m, restoredEntry.NewRefundCap);

        // Los 3 cargos (Fee + Withholding + FacturadaAparte) se borran: un "deshacer" es total, no parcial.
        Assert.Empty(await h.Ctx.BookingCancellationLineOperatorCharges.ToListAsync());
    }

    // ============================================================
    // Caso 5 — SupplierCancellationCircuitReader: "Multa retenida" = RetainedDeductionAmount;
    // FacturadaAparte = linea de deuda AP (OperatorChargeInvoiced), no retencion; Withholding no aparece.
    // ============================================================

    [Fact]
    public async Task CircuitReader_SeparatesRetainedFromInvoicedFromWithholding()
    {
        var h = BuildService();
        var (bc, _, supplier) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Withholding, PenaltyCollectionMode.Retenida, 50m, "ARS"),
            "u", "U", default, userCanClassifyAgencyPenalty: true);
        await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Tax, PenaltyCollectionMode.FacturadaAparte, 80m, "ARS", DocumentRef: "F-1"),
            "u", "U", default, userCanClassifyAgencyPenalty: true);

        var circuit = await SupplierCancellationCircuitReader.LoadAsync(h.Ctx, supplier.Id, default);

        var retainedLine = Assert.Single(circuit.CircuitLines, l => l.Kind == SupplierAccountStatementLineKinds.PenaltyRetained);
        Assert.Equal(300m, retainedLine.Amount); // SOLO el Fee retenido, nunca el Withholding.

        var invoicedLine = Assert.Single(circuit.CircuitLines, l => l.Kind == SupplierAccountStatementLineKinds.OperatorChargeInvoiced);
        Assert.Equal(80m, invoicedLine.Amount);
        Assert.Equal("F-1", invoicedLine.DocumentRef);

        // El Withholding (50) no aparece en NINGUNA de las dos lineas de circuito.
        Assert.DoesNotContain(circuit.CircuitLines, l => l.Amount == 50m);
    }

    // ============================================================
    // Caso 6 — OperatorRefundReadModelService: capBeforePenalty reconstruido con RetainedDeductionAmount.
    // ============================================================

    [Fact]
    public void ReadModel_BuildEstimatedForCurrency_UsesRetainedDeductionAmount_NotPenaltyAmount()
    {
        var line = new BookingCancellationLine
        {
            Currency = "ARS",
            RefundCap = 700m,
            // PenaltyAmount (eje CLIENTE) incluye Fee(300) + Tax facturado aparte(80) = 380, pero
            // RetainedDeductionAmount (eje CAJA) es SOLO 300 (lo unico que salio del cap).
            PenaltyAmount = 380m,
            RetainedDeductionAmount = 300m,
            ReceivedRefundAmount = 0m,
        };

        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "ARS", new System.Collections.Generic.List<BookingCancellationLine> { line }, canSeeCost: true);

        // PaidToOperator (capBeforePenalty reconstruido) = RefundCap + RetainedDeductionAmount = 700+300 = 1000,
        // NO 700+380=1080 (que seria el bug si usara PenaltyAmount).
        Assert.Equal(1000m, dto.PaidToOperator);
        Assert.Equal(300m, dto.PenaltyRetained);
        Assert.Equal(700m, dto.EstimatedAmount);
    }

    // ============================================================
    // Caso 7 — B2 moneda: rechazar un cargo en una moneda distinta a la de la linea.
    // ============================================================

    [Fact]
    public async Task AddOperatorCharge_Rejects_CurrencyMismatch()
    {
        var h = BuildService();
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m, currency: "ARS");
        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Tax, PenaltyCollectionMode.Retenida, 50m, "USD"),
            "u", "U", default, userCanClassifyAgencyPenalty: true));
    }

    // ============================================================
    // Caso 8 — Gate CommissionOnly (Decision A): bloquea el cargo automatico Y el agregado.
    // ============================================================

    [Fact]
    public async Task Allocate_Blocks_When_Supplier_Is_CommissionOnly()
    {
        var h = BuildService();
        var (bc, _, _) = await SeedBcWithLineAsync(
            h.Ctx, refundCap: 1000m, invoicingMode: SupplierInvoicingMode.CommissionOnly);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U"));
        Assert.Equal("INV-ADR044-T2-COMMISSIONONLY", ex.InvariantCode);

        // Nada se creo ni se neteo: la linea sigue con su cap intacto y sin cargos.
        var line = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(1000m, line.RefundCap);
        Assert.Empty(await h.Ctx.BookingCancellationLineOperatorCharges.ToListAsync());
    }

    [Fact]
    public async Task AddOperatorCharge_Blocks_When_Supplier_Is_CommissionOnly()
    {
        var h = BuildService();
        var (bc, line, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        // El operador cambia de modo DESPUES de la confirmacion (dato mas realista: el snapshot de la linea sigue
        // en null/TotalToCustomer porque se creo antes; forzamos el caso "cambio en vivo" tocando el Supplier).
        var supplier = await h.Ctx.Suppliers.SingleAsync();
        supplier.InvoicingMode = SupplierInvoicingMode.CommissionOnly;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() => h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Withholding, PenaltyCollectionMode.Retenida, 50m, "ARS"),
            "u", "U", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR044-T2-COMMISSIONONLY", ex.InvariantCode);
    }

    // ============================================================
    // Caso 9 — Linea legacy sin charges (solo escalares) se comporta byte-identico; SupplierInvoicingModeAtEvent
    // null cae al fallback vivo del Supplier sin excepcion.
    // ============================================================

    [Fact]
    public async Task Legacy_Line_Without_Charges_FallsBackToLiveSupplierInvoicingMode_NoException()
    {
        var h = BuildService();
        // SupplierInvoicingModeAtEvent queda NULL a proposito (linea "legacy", ver SeedBcWithLineAsync). El
        // Supplier vive con su default TotalToCustomer (no CommissionOnly), asi que el fallback vivo deja pasar
        // el neteo SIN excepcion — comportamiento identico al de antes de esta tanda.
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 500m);

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 200m, "ARS", default, userId: "u", userName: "U");
        await h.Ctx.SaveChangesAsync();

        var line = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Null(line.SupplierInvoicingModeAtEvent);
        Assert.Equal(300m, line.RefundCap);
        Assert.Equal(200m, line.RetainedDeductionAmount);
    }

    // ============================================================
    // Contrato JSON: GetOperatorPenaltySituationsAsync expone los cargos por operador.
    // ============================================================

    /// <summary>
    /// Semilla post-NC vigente (factura + NC con CAE) con multa confirmada + UNA linea con UN cargo Fee/Retenida
    /// de 300 ARS. Devuelve el PublicId de la reserva para consultar el read-model.
    /// </summary>
    private static async Task<Guid> SeedConfirmedWithOneChargeAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador X", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-DTO", Name = "Reserva Test", PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 100, CAE = "1", Resultado = "A",
            MonId = "PES", ImporteTotal = 100_000m, ImporteNeto = 100_000m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 101, CAE = "2", Resultado = "A",
            ReservaId = reserva.Id, OriginalInvoiceId = original.Id,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund, Reason = "Cliente anulo",
            DraftedByUserId = "vendedor-1", PenaltyStatus = PenaltyStatus.Confirmed, PenaltyAmountAtEvent = 300m,
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-10),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id, Currency = "ARS",
            RefundCap = 700m, RetainedDeductionAmount = 300m, PenaltyAmount = 300m,
            PenaltyStatus = PenaltyStatus.Confirmed,
        });
        await ctx.SaveChangesAsync();

        var line = await ctx.BookingCancellationLines.SingleAsync();
        ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLine = line, Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = PenaltyCollectionMode.Retenida, Amount = 300m, Currency = "ARS",
            ConfirmedByUserId = "u", ConfirmedByUserName = "U",
        });
        await ctx.SaveChangesAsync();

        return reserva.PublicId;
    }

    [Fact]
    public async Task GetOperatorPenaltySituations_ExposesChargesList()
    {
        var h = BuildService();
        var reservaPublicId = await SeedConfirmedWithOneChargeAsync(h.Ctx);

        var situations = await h.Service.GetOperatorPenaltySituationsAsync(
            reservaPublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default, canSeeCost: true);

        var situation = Assert.Single(situations);
        var charge = Assert.Single(situation.Charges);
        Assert.Equal("AdministrativeFee", charge.Kind);
        Assert.Equal("Retenida", charge.CollectionMode);
        Assert.Equal(300m, charge.Amount);
        Assert.Equal("ARS", charge.Currency);
    }

    // ============================================================
    // SECURITY (menor): Charges se ENMASCARA sin visibilidad de costo.
    // ============================================================

    [Fact]
    public async Task GetOperatorPenaltySituations_WithoutSeeCost_MasksChargesList()
    {
        var h = BuildService();
        var reservaPublicId = await SeedConfirmedWithOneChargeAsync(h.Ctx);

        var situations = await h.Service.GetOperatorPenaltySituationsAsync(
            reservaPublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default, canSeeCost: false);

        // Sin cobranzas.see_cost: la situacion sigue existiendo (el usuario ve el ESTADO de la multa), pero el
        // desglose de cargos (dato de COSTO) viaja VACIO — mismo criterio que PenaltyRetained/PaidToOperator.
        var situation = Assert.Single(situations);
        Assert.Empty(situation.Charges);
    }

    // ============================================================
    // BLOQUEANTE 1 (security): NUNCA aplicar parcial cuando el cap no alcanza.
    // ============================================================

    [Fact]
    public async Task AddOperatorCharge_Retenida_CapExhausted_Rejects_INV_CHARGE_002_NoAuditNoPersist()
    {
        var h = BuildService();
        // Cap = 300 y la multa base se lo come TODO (RefundCap 0 tras el Allocate).
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 300m);
        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        var lineBefore = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(0m, lineBefore.RefundCap); // cap agotado por la multa base.

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() => h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Tax, PenaltyCollectionMode.Retenida, 50m, "ARS"),
            "u", "U", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR044-CHARGE-002", ex.InvariantCode);

        // NADA se persistio: solo el cargo base (Fee) sigue existiendo; no se creo el Tax.
        var charges = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().ToListAsync();
        Assert.Single(charges);
        Assert.Equal(OperatorChargeKind.AdministrativeFee, charges[0].Kind);
        // El audit del cargo NUNCA se emitio (ni siquiera parcial).
        h.AuditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.OperatorChargeAdded, It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AddOperatorCharge_Retenida_CapPartial_Rejects_INV_CHARGE_002_NoTruncation()
    {
        var h = BuildService();
        // Cap remanente 100 tras la multa base; pedimos retener 150 (> lo que queda) -> todo-o-nada rechaza.
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 400m);
        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        var lineBefore = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(100m, lineBefore.RefundCap); // 400 - 300 base = 100 remanente.

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() => h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Tax, PenaltyCollectionMode.Retenida, 150m, "ARS"),
            "u", "U", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR044-CHARGE-002", ex.InvariantCode);

        // El cap NO se truncó parcialmente: sigue en 100, sin cargo Tax nuevo.
        var lineAfter = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(100m, lineAfter.RefundCap);
        Assert.Single(await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AddOperatorCharge_FacturadaAparte_CapZero_OK_DoesNotTouchCap()
    {
        var h = BuildService();
        // Cap agotado por la multa base, pero FacturadaAparte NO toca caja -> se permite igual.
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 300m);
        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Tax, PenaltyCollectionMode.FacturadaAparte, 50m, "ARS", DocumentRef: "F-9"),
            "u", "U", default, userCanClassifyAgencyPenalty: true);

        var line = await h.Ctx.BookingCancellationLines.AsNoTracking().SingleAsync();
        Assert.Equal(0m, line.RefundCap); // sin cambios: FacturadaAparte no resta.
        var facturada = await h.Ctx.BookingCancellationLineOperatorCharges
            .AsNoTracking().SingleAsync(c => c.CollectionMode == PenaltyCollectionMode.FacturadaAparte);
        Assert.Equal(50m, facturada.Amount);
    }

    // ============================================================
    // BLOQUEANTE 2 (backend): dedup por doble submit (ventana 60s).
    // ============================================================

    [Fact]
    public async Task AddOperatorCharge_DoubleSubmit_SecondRejects_INV_CHARGE_DUP_NoDuplicatePlata()
    {
        var h = BuildService();
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);
        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        var request = new AddOperatorChargeRequest(OperatorChargeKind.Withholding, PenaltyCollectionMode.Retenida, 50m, "ARS");

        // Primer submit: OK.
        await h.Service.AddOperatorChargeAsync(bc.PublicId, request, "u", "U", default, userCanClassifyAgencyPenalty: true);

        // Segundo submit IDENTICO dentro de la ventana: rebota 409 dedup, sin duplicar el cargo.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() => h.Service.AddOperatorChargeAsync(
            bc.PublicId, request, "u", "U", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR044-CHARGE-DUP", ex.InvariantCode);

        // Solo hay 2 cargos: el Fee base + UN Withholding (no dos).
        var charges = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().ToListAsync();
        Assert.Equal(2, charges.Count);
        Assert.Single(charges, c => c.Kind == OperatorChargeKind.Withholding);
    }

    // ============================================================
    // Menor 2: corregir la multa base DESTRUYE los cargos secundarios (intencional).
    // ============================================================

    [Fact]
    public async Task CorrectPenalty_DestroysSecondaryCharges_ByDesign()
    {
        var h = BuildService();
        var (bc, _, _) = await SeedBcWithLineAsync(h.Ctx, refundCap: 1000m);
        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", default, userId: "u", userName: "U");
        // La correccion exige que el BC-padre este Confirmed y con moneda declarada (post-NC vigente lo tiene por
        // SeedBcWithLineAsync). Marcamos Confirmed + moneda para que el flujo corra.
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.PenaltyAmountAtEvent = 300m;
        bc.PenaltyCurrencyAtEvent = "ARS";
        bc.DebitNoteStatus = DebitNoteStatus.ManualReview; // ND trabada, sin CAE (habilita corregir).
        await h.Ctx.SaveChangesAsync();

        // Agregamos un cargo secundario Withholding.
        await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Withholding, PenaltyCollectionMode.Retenida, 50m, "ARS"),
            "u", "U", default, userCanClassifyAgencyPenalty: true);
        Assert.Equal(2, await h.Ctx.BookingCancellationLineOperatorCharges.CountAsync());

        // Corregir el monto/moneda de la multa base: el Reverse borra TODOS los cargos (comportamiento intencional).
        await h.Service.CorrectPenaltyAsync(
            bc.PublicId, amount: 400m, currency: "ARS", reason: "El operador cambió el monto de la multa.",
            userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true);

        // Tras la correccion: el Withholding secundario desaparecio; queda SOLO el cargo Fee automatico nuevo (400).
        var charges = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().ToListAsync();
        Assert.Single(charges);
        Assert.Equal(OperatorChargeKind.AdministrativeFee, charges[0].Kind);
        Assert.Equal(400m, charges[0].Amount);
        // El audit de la correccion dejo la foto de los cargos borrados (menor 1).
        h.AuditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.OperatorPenaltyCorrected, It.IsAny<string>(), It.IsAny<string>(),
            It.Is<string>(d => d.Contains("deletedOperatorCharges") && d.Contains("Withholding")),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    // ============================================================
    // Menor 3: reparto multi-linea de DistributeChargeAcrossLines (2-3 lineas).
    // ============================================================

    [Fact]
    public async Task AddOperatorCharge_MultiLine_Retenida_DistributesProportionalToRefundCap()
    {
        var h = BuildService();
        // Semilla con 3 lineas del MISMO operador (mismo SupplierId, misma moneda), caps 600/300/100 (total 1000).
        var (bc, line1, supplier) = await SeedBcWithLineAsync(h.Ctx, refundCap: 600m);
        var line2 = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 2, Scope = BookingCancellationLineScope.Full, Currency = "ARS",
            LineSaleAmount = 300m, RefundCap = 300m, PenaltyStatus = PenaltyStatus.Confirmed,
        };
        var line3 = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 3, Scope = BookingCancellationLineScope.Full, Currency = "ARS",
            LineSaleAmount = 100m, RefundCap = 100m, PenaltyStatus = PenaltyStatus.Confirmed,
        };
        // line1 debe quedar Confirmed tambien (el gate exige multa confirmada).
        line1.PenaltyStatus = PenaltyStatus.Confirmed;
        h.Ctx.BookingCancellationLines.AddRange(line2, line3);
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await h.Ctx.SaveChangesAsync();

        // Retener 200 (Tax/Retenida): se reparte proporcional a los caps (600/300/100 sobre 1000) = 120/60/20.
        await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(OperatorChargeKind.Tax, PenaltyCollectionMode.Retenida, 200m, "ARS"),
            "u", "U", default, userCanClassifyAgencyPenalty: true);

        var charges = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking()
            .Where(c => c.Kind == OperatorChargeKind.Tax)
            .ToListAsync();
        // La suma de las porciones == 200 EXACTO (la ultima linea absorbe el residuo).
        Assert.Equal(200m, charges.Sum(c => c.Amount));
        Assert.Equal(3, charges.Count);

        var lines = await h.Ctx.BookingCancellationLines.AsNoTracking().OrderBy(l => l.ServiceId).ToListAsync();
        // Los caps bajaron y la suma de RetainedDeductionAmount de las 3 lineas == 200.
        Assert.Equal(200m, lines.Sum(l => l.RetainedDeductionAmount));
        // Ninguna linea quedo con cap negativo.
        Assert.All(lines, l => Assert.True(l.RefundCap >= 0m));
    }
}
