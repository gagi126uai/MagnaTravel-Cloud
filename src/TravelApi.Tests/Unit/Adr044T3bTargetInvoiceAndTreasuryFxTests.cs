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
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 T3b (2026-07-10): los 9 tests obligatorios del Addendum — resolucion de factura destino con 2+
/// facturas activas (Decision 1), conversion de moneda de un cargo embebido (Decision 2), y el ajuste de
/// diferencia de cambio de tesoreria al liquidar (Decision 3), incluida su lectura en el extracto del operador
/// (M3) y el supersede/recalculo al anular/reemplazar la liquidacion de origen (M4).
///
/// <para><b>Estrategia de siembra</b> (Parte A, tests 1-5): mismo enfoque que
/// <see cref="Adr044T3aMultiOperatorDebitNoteTests"/> — arma el BC ya CONFIRMADO a mano y usa
/// <c>RetryDebitNoteEmissionAsync</c> para disparar <c>TryEmitCancellationDebitNoteAsync</c> con control total
/// sobre los cargos y las facturas activas.</para>
///
/// <para><b>Estrategia de siembra</b> (Parte B, tests 6-9): <see cref="TreasuryFxAdjustmentEngine"/> y
/// <see cref="SupplierCancellationCircuitReader"/> se ejercitan DIRECTO contra un <see cref="AppDbContext"/>
/// InMemory, sin pasar por <c>OperatorRefundService</c>/<c>SupplierService</c> completos (evita el peso de su
/// setup para aislar la logica de Decision 3).</para>
/// </summary>
public class Adr044T3bTargetInvoiceAndTreasuryFxTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr044-t3b-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    // =====================================================================================
    // Parte A (tests 1-5): Decision 1 (TargetInvoiceId) + Decision 2 (conversion de moneda),
    // ejercitadas via BookingCancellationService (mismo harness que T3a).
    // =====================================================================================

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
                EnableMultiCurrencyInvoicing = true,
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
    /// Siembra un BC ya CONFIRMADO (pass-through, listo para reintentar la emision) con UNA factura original en
    /// pesos. El caller agrega, si el test lo necesita, una SEGUNDA factura activa (2+ facturas) llamando
    /// <see cref="AddSecondActiveInvoiceAsync"/>.
    /// </summary>
    private static async Task<(BookingCancellation Bc, Invoice Original, Reserva Reserva)> SeedConfirmedReadyToRetryAsync(
        AppDbContext ctx, Supplier primarySupplier, decimal originalTotal = 500_000m)
    {
        var customer = new Customer { FullName = "Cliente T3b", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T3B", Name = "Reserva T3b", PayerId = customer.Id,
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
            Reason = "Cancelacion T3b",
            DraftedByUserId = "vendedor-1", ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-5),
            ConfirmedByUserId = "vendedor-1",
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            PenaltyStatus = PenaltyStatus.Confirmed,
            ConceptClassifiedByUserId = "u1", ConceptClassifiedByUserName = "U1",
            ConceptClassifiedAt = DateTime.UtcNow.AddDays(-1),
            PenaltyConfirmedByUserId = "u1", PenaltyConfirmedByUserName = "U1",
            PenaltyConfirmedAt = DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge,
            PenaltyAmountAtEvent = 1m,
            PenaltyCurrencyAtEvent = "ARS",
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

        return (bc, original, reserva);
    }

    /// <summary>Agrega una SEGUNDA factura de venta activa (con CAE) a la reserva, en USD por defecto — el caso "2+ facturas activas" (ADR-042).</summary>
    private static async Task<Invoice> AddSecondActiveInvoiceAsync(
        AppDbContext ctx, Reserva reserva, string monId = "DOL", decimal monCotiz = 1000m, decimal importeTotal = 300m,
        bool annulled = false)
    {
        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 950 + (int)(DateTime.UtcNow.Ticks % 1000),
            CAE = "cae-second", Resultado = "A", MonId = monId, MonCotiz = monCotiz, ImporteTotal = importeTotal,
            ReservaId = reserva.Id,
            AnnulmentStatus = annulled ? AnnulmentStatus.Succeeded : AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        return invoice;
    }

    private static async Task<BookingCancellationLine> AddConfirmedLineWithChargeAsync(
        AppDbContext ctx, BookingCancellation bc, Supplier supplier, decimal amount, string currency = "ARS",
        int? targetInvoiceId = null, decimal? estimatedRate = null)
    {
        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = ctx.BookingCancellationLines.Count() + 1,
            Scope = BookingCancellationLineScope.Full, Currency = currency,
            RefundCap = 0m, PenaltyAmount = amount, RetainedDeductionAmount = amount,
            PenaltyStatus = PenaltyStatus.Confirmed,
        };
        ctx.BookingCancellationLines.Add(line);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = line.Id,
            Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = PenaltyCollectionMode.Retenida,
            Amount = amount,
            Currency = currency,
            ClientTransferMode = ClientTransferMode.AsIs,
            ConfirmedByUserId = "u1",
            ConfirmedByUserName = "U1",
            ConfirmedAt = DateTime.UtcNow.AddDays(-1),
            TargetInvoiceId = targetInvoiceId,
            EstimatedExchangeRateToClientInvoiceCurrency = estimatedRate,
            EstimatedExchangeRateSource = estimatedRate.HasValue ? ExchangeRateSource.Manual : null,
            EstimatedExchangeRateAt = estimatedRate.HasValue ? DateTime.UtcNow.AddDays(-1) : null,
            EstimatedExchangeRateJustification = estimatedRate.HasValue ? "TC manual cargado al confirmar." : null,
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

    // ============================================================
    // 1) Regresion mono-moneda: 1 factura activa, sin cambios (byte-identico a T3a).
    // ============================================================

    [Fact]
    public async Task SingleActiveInvoice_SameCurrency_EmitsLikeT3a_NoEstimatedFields()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, original, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        // Sin TargetInvoiceId explicito: con 1 sola factura activa, el motor la autocompleta sola.
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m, currency: "ARS");

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r =>
                r.Items.Count == 1 && r.Items[0].Total == 20_000m && r.MonId == original.MonId),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // El cargo no necesito conversion: sus campos de TC quedan sin tocar.
        var charge = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().SingleAsync();
        Assert.Null(charge.DefinitiveExchangeRateAtNdEmission);
    }

    // ============================================================
    // 2) 2 facturas + seleccion correcta: el cargo con TargetInvoiceId explicito emite en ESA factura.
    // ============================================================

    [Fact]
    public async Task TwoActiveInvoices_ChargeTargetsSecond_EmitsAgainstThatInvoice()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        var secondInvoice = await AddSecondActiveInvoiceAsync(h.Ctx, reserva, monId: "DOL", monCotiz: 1000m, importeTotal: 300m);

        // El cargo esta en USD y elige explicitamente la SEGUNDA factura (tambien en USD: sin cruce de moneda,
        // aisla Decision 1 de Decision 2).
        await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierA, amount: 100m, currency: "USD", targetInvoiceId: secondInvoice.Id);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.Is<CreateInvoiceRequest>(r =>
                r.OriginalInvoiceId == secondInvoice.PublicId.ToString() &&
                r.MonId == "DOL" && r.MonCotiz == 1000m &&
                r.Items.Count == 1 && r.Items[0].Total == 100m),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // 3) 2 facturas sin eleccion: TargetInvoiceId null -> manual, NO emite ND automatica.
    // ============================================================

    [Fact]
    public async Task TwoActiveInvoices_ChargeWithoutTargetInvoice_RoutesManual()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        await AddSecondActiveInvoiceAsync(h.Ctx, reserva);

        // Sin TargetInvoiceId: con 2+ facturas activas, el motor NO adivina.
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m, currency: "ARS", targetInvoiceId: null);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Contains("factura", reloaded.DebitNoteArcaErrorMessage);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 4) B2 — factura destino anulada al emitir: rutea a manual, nunca contra una factura muerta.
    // ============================================================

    [Fact]
    public async Task TargetInvoiceAnnulledBeforeEmission_RoutesManual()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        // Dos facturas siguen ACTIVAS (para que el caso siga siendo "2+ facturas activas")...
        await AddSecondActiveInvoiceAsync(h.Ctx, reserva, monId: "DOL", monCotiz: 1000m);
        // ...pero el cargo eligio una TERCERA factura que YA se anulo entre el confirmar y el emitir.
        var thirdInvoiceNowAnnulled = await AddSecondActiveInvoiceAsync(
            h.Ctx, reserva, monId: "PES", monCotiz: 1m, importeTotal: 999m, annulled: true);

        await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierA, amount: 20_000m, currency: "ARS", targetInvoiceId: thirdInvoiceNowAnnulled.Id);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Contains("ya no está activa", reloaded.DebitNoteArcaErrorMessage);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 5) M2 — invariante de TargetInvoiceId compartido dentro de la MISMA linea.
    // ============================================================

    [Fact]
    public async Task AddOperatorCharge_ConflictingTargetInvoiceOnSameLine_Rejected()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        var secondInvoice = await AddSecondActiveInvoiceAsync(h.Ctx, reserva);
        // El cargo BASE de la linea ya quedo apuntando a la factura ORIGINAL (original.Id).
        var originalInvoiceId = bc.OriginatingInvoiceId;
        await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierA, amount: 20_000m, currency: "ARS", targetInvoiceId: originalInvoiceId);

        // Intentar agregar OTRO cargo (Tax, no-Withholding) de la MISMA linea/operador apuntando a la SEGUNDA
        // factura: choca con M2 (misma linea, dos facturas destino distintas).
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.AddOperatorChargeAsync(
                bc.PublicId,
                new AddOperatorChargeRequest(
                    Kind: OperatorChargeKind.Tax,
                    CollectionMode: PenaltyCollectionMode.Retenida,
                    Amount: 1_000m,
                    Currency: "ARS",
                    TargetInvoicePublicId: secondInvoice.PublicId),
                userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR044-TARGETINVOICE-002", ex.InvariantCode);

        // Un Withholding SI puede tener una factura destino distinta (o ninguna): nunca choca (nunca llega al
        // cliente, exento de M2).
        var dto = await h.Service.AddOperatorChargeAsync(
            bc.PublicId,
            new AddOperatorChargeRequest(
                Kind: OperatorChargeKind.Withholding,
                CollectionMode: PenaltyCollectionMode.Retenida,
                Amount: 500m,
                Currency: "ARS",
                TargetInvoicePublicId: secondInvoice.PublicId),
            userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true);
        Assert.NotNull(dto);
    }

    // =====================================================================================
    // Parte B (tests 6-9): Decision 3, ejercitada directo contra TreasuryFxAdjustmentEngine +
    // SupplierCancellationCircuitReader (sin pasar por OperatorRefundService/SupplierService completos).
    // =====================================================================================

    /// <summary>Siembra el minimo BC + Line + Charge (Retenida, con TC definitivo ya fijado) para probar Decision 3.</summary>
    private static async Task<(BookingCancellation Bc, BookingCancellationLine Line, BookingCancellationLineOperatorCharge Charge, Supplier Supplier)>
        SeedChargeWithDefinitiveRateAsync(
            AppDbContext ctx, decimal chargeAmount = 100m, string chargeCurrency = "USD",
            decimal definitiveRate = 1000m, PenaltyCollectionMode collectionMode = PenaltyCollectionMode.Retenida)
    {
        var customer = new Customer { FullName = "Cliente FX", IsActive = true };
        ctx.Customers.Add(customer);
        var supplier = new Supplier { Name = "Operador FX", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva { NumeroReserva = "R-FX", Name = "Reserva FX", Balance = 0m };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var arsInvoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 1, CAE = "cae-fx",
            Resultado = "A", MonId = "PES", ImporteTotal = 500_000m, ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(arsInvoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = arsInvoice.Id, Reason = "Cancelacion FX",
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Manual, FetchedAt = DateTime.UtcNow },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = chargeCurrency,
            RefundCap = 0m, PenaltyStatus = PenaltyStatus.Confirmed,
        };
        ctx.BookingCancellationLines.Add(line);
        await ctx.SaveChangesAsync();

        var charge = new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = line.Id,
            Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = collectionMode,
            Amount = chargeAmount,
            Currency = chargeCurrency,
            TargetInvoiceId = arsInvoice.Id,
            DefinitiveExchangeRateAtNdEmission = definitiveRate,
            DefinitiveExchangeRateSource = ExchangeRateSource.Manual,
            DefinitiveExchangeRateAt = DateTime.UtcNow.AddDays(-1),
            ConfirmedByUserId = "u1",
        };
        ctx.BookingCancellationLineOperatorCharges.Add(charge);
        await ctx.SaveChangesAsync();

        return (bc, line, charge, supplier);
    }

    // ============================================================
    // 6) Delta FX con signo correcto (Retenida), ambas direcciones.
    // ============================================================

    [Fact]
    public async Task RetainedCharge_SettlementBetterThanNdRate_PositiveDeltaInFavorOfAgency()
    {
        var ctx = NewDbContext();
        var (bc, _, charge, supplier) = await SeedChargeWithDefinitiveRateAsync(ctx, chargeAmount: 100m, definitiveRate: 1000m);

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id, ReceivedAmount = 100_000m, Currency = "USD",
            ExchangeRateAtReceipt = 1100m, ReceivedByUserId = "cashier",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id, BookingCancellationId = bc.Id,
            GrossAmount = 100_000m, NetAmount = 100_000m, CreatedByUserId = "cashier",
        };
        ctx.OperatorRefundAllocations.Add(allocation);
        await ctx.SaveChangesAsync();

        var trackedAllocation = await ctx.OperatorRefundAllocations
            .Include(a => a.Refund)
            .SingleAsync(a => a.Id == allocation.Id);

        await TreasuryFxAdjustmentEngine.RegisterForRetainedChargesAsync(ctx, trackedAllocation, null, default);
        await ctx.SaveChangesAsync();

        var adjustment = await ctx.BookingCancellationLineTreasuryFxAdjustments
            .AsNoTracking().SingleAsync(a => a.OperatorChargeId == charge.Id);
        Assert.Equal(10_000m, adjustment.DeltaAmount); // (1100-1000) x 100, a favor de la agencia.
        Assert.Equal(allocation.Id, adjustment.OperatorRefundAllocationId);
        Assert.Null(adjustment.SupplierPaymentId);
        Assert.False(adjustment.IsSuperseded);
    }

    [Fact]
    public async Task RetainedCharge_SettlementWorseThanNdRate_NegativeDeltaAgainstAgency()
    {
        var ctx = NewDbContext();
        var (bc, _, charge, supplier) = await SeedChargeWithDefinitiveRateAsync(ctx, chargeAmount: 100m, definitiveRate: 1000m);

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id, ReceivedAmount = 90_000m, Currency = "USD",
            ExchangeRateAtReceipt = 900m, ReceivedByUserId = "cashier",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id, BookingCancellationId = bc.Id,
            GrossAmount = 90_000m, NetAmount = 90_000m, CreatedByUserId = "cashier",
        };
        ctx.OperatorRefundAllocations.Add(allocation);
        await ctx.SaveChangesAsync();

        var trackedAllocation = await ctx.OperatorRefundAllocations
            .Include(a => a.Refund)
            .SingleAsync(a => a.Id == allocation.Id);

        await TreasuryFxAdjustmentEngine.RegisterForRetainedChargesAsync(ctx, trackedAllocation, null, default);
        await ctx.SaveChangesAsync();

        var adjustment = await ctx.BookingCancellationLineTreasuryFxAdjustments
            .AsNoTracking().SingleAsync(a => a.OperatorChargeId == charge.Id);
        Assert.Equal(-10_000m, adjustment.DeltaAmount); // (900-1000) x 100, en contra de la agencia.
    }

    // ============================================================
    // 7) FacturadaAparte cross-currency: el ajuste se dispara al registrar el SupplierPayment.
    // ============================================================

    [Fact]
    public async Task InvoicedCharge_CrossCurrencySupplierPayment_RegistersAdjustmentWithSupplierPaymentOrigin()
    {
        var ctx = NewDbContext();
        var (_, _, charge, supplier) = await SeedChargeWithDefinitiveRateAsync(
            ctx, chargeAmount: 100m, definitiveRate: 1000m, collectionMode: PenaltyCollectionMode.FacturadaAparte);

        var payment = new SupplierPayment
        {
            SupplierId = supplier.Id, Amount = 100m, Currency = "USD",
            ImputedCurrency = "ARS", ExchangeRate = 1050m, ExchangeRateSource = ExchangeRateSource.Manual,
            ImputedAmount = 105_000m,
        };
        ctx.SupplierPayments.Add(payment);
        await ctx.SaveChangesAsync();

        await TreasuryFxAdjustmentEngine.RegisterForInvoicedChargeAsync(ctx, charge, payment, null, default);
        await ctx.SaveChangesAsync();

        var adjustment = await ctx.BookingCancellationLineTreasuryFxAdjustments
            .AsNoTracking().SingleAsync(a => a.OperatorChargeId == charge.Id);
        Assert.Equal(5_000m, adjustment.DeltaAmount); // (1050-1000) x 100.
        Assert.Equal(payment.Id, adjustment.SupplierPaymentId);
        Assert.Null(adjustment.OperatorRefundAllocationId);
    }

    // ============================================================
    // 8) M4 — supersede/recalculo: voidear la allocation de origen marca superseded; el reemplazo enlaza.
    // ============================================================

    [Fact]
    public async Task VoidingAllocation_SupersedesAdjustment_ReplacementLinksToIt()
    {
        var ctx = NewDbContext();
        var (bc, _, charge, supplier) = await SeedChargeWithDefinitiveRateAsync(ctx, chargeAmount: 100m, definitiveRate: 1000m);

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id, ReceivedAmount = 100_000m, Currency = "USD",
            ExchangeRateAtReceipt = 1100m, ReceivedByUserId = "cashier",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id, BookingCancellationId = bc.Id,
            GrossAmount = 100_000m, NetAmount = 100_000m, CreatedByUserId = "cashier",
        };
        ctx.OperatorRefundAllocations.Add(allocation);
        await ctx.SaveChangesAsync();

        var trackedAllocation = await ctx.OperatorRefundAllocations
            .Include(a => a.Refund).SingleAsync(a => a.Id == allocation.Id);
        await TreasuryFxAdjustmentEngine.RegisterForRetainedChargesAsync(ctx, trackedAllocation, null, default);
        await ctx.SaveChangesAsync();
        var oldAdjustment = await ctx.BookingCancellationLineTreasuryFxAdjustments
            .SingleAsync(a => a.OperatorChargeId == charge.Id);

        // Se voidea la allocation (correccion): el ajuste vigente queda superseded.
        var superseded = await TreasuryFxAdjustmentEngine.SupersedeForVoidedOriginAsync(
            ctx, default, voidedOperatorRefundAllocationId: allocation.Id);
        await ctx.SaveChangesAsync();
        Assert.Single(superseded);
        Assert.True((await ctx.BookingCancellationLineTreasuryFxAdjustments.AsNoTracking()
            .SingleAsync(a => a.Id == oldAdjustment.Id)).IsSuperseded);

        // Llega la allocation de REEMPLAZO (TC de recibo corregido) sobre el MISMO cargo: se calcula una fila
        // nueva, y se enlaza a la vieja.
        var replacementRefund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id, ReceivedAmount = 105_000m, Currency = "USD",
            ExchangeRateAtReceipt = 1050m, ReceivedByUserId = "cashier",
        };
        ctx.OperatorRefundReceived.Add(replacementRefund);
        await ctx.SaveChangesAsync();
        var replacementAllocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = replacementRefund.Id, BookingCancellationId = bc.Id,
            GrossAmount = 105_000m, NetAmount = 105_000m, CreatedByUserId = "cashier",
        };
        ctx.OperatorRefundAllocations.Add(replacementAllocation);
        await ctx.SaveChangesAsync();
        var trackedReplacement = await ctx.OperatorRefundAllocations
            .Include(a => a.Refund).SingleAsync(a => a.Id == replacementAllocation.Id);

        await TreasuryFxAdjustmentEngine.RegisterForRetainedChargesAsync(ctx, trackedReplacement, null, default);
        await ctx.SaveChangesAsync();

        var newAdjustment = await ctx.BookingCancellationLineTreasuryFxAdjustments
            .SingleAsync(a => a.OperatorChargeId == charge.Id && !a.IsSuperseded);
        TreasuryFxAdjustmentEngine.LinkSupersededTo(
            await ctx.BookingCancellationLineTreasuryFxAdjustments.SingleAsync(a => a.Id == oldAdjustment.Id),
            newAdjustment);
        await ctx.SaveChangesAsync();

        var oldReloaded = await ctx.BookingCancellationLineTreasuryFxAdjustments
            .AsNoTracking().SingleAsync(a => a.Id == oldAdjustment.Id);
        Assert.Equal(newAdjustment.Id, oldReloaded.SupersededByAdjustmentId);
        // El indice unico filtrado sigue permitiendo UNA sola vigente por cargo.
        var vigentesCount = await ctx.BookingCancellationLineTreasuryFxAdjustments
            .CountAsync(a => a.OperatorChargeId == charge.Id && !a.IsSuperseded);
        Assert.Equal(1, vigentesCount);
    }

    // ============================================================
    // 9) M3 + K2 — lectura: el ajuste vigente aparece en el extracto (superseded NO), en el bloque de la moneda
    //    de la LINEA (no de liquidacion), con el delta convertido coherente a esa moneda.
    // ============================================================

    [Fact]
    public async Task CircuitReader_ShowsOnlyVigenteTreasuryFxAdjustment_InLineCurrencyBlock_Converted()
    {
        var ctx = NewDbContext();
        // charge/line en USD; la ND (SettlementCurrency) fue en ARS -> el ajuste se guarda en ARS pero se pinta
        // en el bloque USD (K2), convirtiendo el delta al TC de la liquidacion.
        var (_, _, charge, supplier) = await SeedChargeWithDefinitiveRateAsync(ctx, chargeAmount: 110m, definitiveRate: 1000m);

        ctx.BookingCancellationLineTreasuryFxAdjustments.Add(new BookingCancellationLineTreasuryFxAdjustment
        {
            OperatorChargeId = charge.Id, SupplierPaymentId = 999, RateAtNdEmission = 1000m,
            RateAtSettlement = 900m, ChargeAmount = 110m, ChargeCurrency = "USD",
            DeltaAmount = -11_000m, SettlementCurrency = "ARS", IsSuperseded = true, // vieja, NO deberia pintarse
        });
        ctx.BookingCancellationLineTreasuryFxAdjustments.Add(new BookingCancellationLineTreasuryFxAdjustment
        {
            OperatorChargeId = charge.Id, SupplierPaymentId = 1000, RateAtNdEmission = 1000m,
            RateAtSettlement = 1100m, ChargeAmount = 110m, ChargeCurrency = "USD",
            DeltaAmount = 11_000m, SettlementCurrency = "ARS", IsSuperseded = false, // vigente (delta 11.000 ARS)
        });
        await ctx.SaveChangesAsync();

        var result = await SupplierCancellationCircuitReader.LoadAsync(ctx, supplier.Id, default);

        var fxLines = result.CircuitLines
            .Where(l => l.Kind == SupplierAccountStatementLineKinds.TreasuryFxAdjustment)
            .ToList();
        var line = Assert.Single(fxLines);
        // K2: bloque = moneda de la LINEA (USD); monto = 11.000 ARS / 1100 (TC liquidacion) = 10 USD; la moneda
        // de liquidacion queda como dato informativo en la descripcion.
        Assert.Equal("USD", line.Currency);
        Assert.Equal(10m, line.Amount);
        Assert.Contains("liquidada en pesos", line.Description);
    }

    // ============================================================
    // K2 (reconciliacion) — multa retenida + su ajuste FX conviven en el MISMO bloque de moneda.
    // ============================================================

    [Fact]
    public async Task CircuitReader_FxAdjustmentAndPenalty_SameCurrencyBlock()
    {
        var ctx = NewDbContext();
        var (_, line, charge, supplier) = await SeedChargeWithDefinitiveRateAsync(ctx, chargeAmount: 110m, definitiveRate: 1000m);
        // La linea tiene multa retenida en USD (misma moneda de la linea).
        line.RetainedDeductionAmount = 110m;
        ctx.BookingCancellationLineTreasuryFxAdjustments.Add(new BookingCancellationLineTreasuryFxAdjustment
        {
            OperatorChargeId = charge.Id, SupplierPaymentId = 1000, RateAtNdEmission = 1000m,
            RateAtSettlement = 1100m, ChargeAmount = 110m, ChargeCurrency = "USD",
            DeltaAmount = 11_000m, SettlementCurrency = "ARS", IsSuperseded = false,
        });
        await ctx.SaveChangesAsync();

        var result = await SupplierCancellationCircuitReader.LoadAsync(ctx, supplier.Id, default);
        var reconciliation = SupplierAccountReconciliationBuilder.Build(
            new Dictionary<string, decimal>(), result.CircuitLines, result.ReceivableByCurrency);

        // Las DOS lineas (multa retenida + diferencia de cambio) caen en el bloque USD, no en bloques separados.
        var usdBlock = reconciliation.Currencies.Single(c => c.Currency == "USD");
        Assert.Contains(usdBlock.CircuitLines, l => l.Kind == SupplierAccountStatementLineKinds.PenaltyRetained);
        Assert.Contains(usdBlock.CircuitLines, l => l.Kind == SupplierAccountStatementLineKinds.TreasuryFxAdjustment);
        Assert.Equal(10m, usdBlock.TreasuryFxAdjustmentTotal);
        Assert.DoesNotContain(reconciliation.Currencies, c => c.Currency == "ARS");
    }

    // ============================================================
    // S1/F1 — banda de sanidad del TC (== 1 = default peligroso): rechazo al cargar y al emitir.
    // ============================================================

    [Fact]
    public async Task AddOperatorCharge_ExchangeRateOne_Rejected()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        var secondInvoice = await AddSecondActiveInvoiceAsync(h.Ctx, reserva, monId: "PES", monCotiz: 1m);
        // Cargo base para poder agregar uno secundario.
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m, currency: "ARS", targetInvoiceId: bc.OriginatingInvoiceId);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.AddOperatorChargeAsync(
                bc.PublicId,
                new AddOperatorChargeRequest(
                    Kind: OperatorChargeKind.Tax,
                    CollectionMode: PenaltyCollectionMode.Retenida,
                    Amount: 1_000m,
                    Currency: "ARS",
                    TargetInvoicePublicId: null,
                    EstimatedExchangeRateToClientInvoiceCurrency: 1m, // default peligroso
                    EstimatedExchangeRateSource: ExchangeRateSource.Manual,
                    EstimatedExchangeRateAt: DateTime.UtcNow,
                    EstimatedExchangeRateJustification: "x"),
                userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true));
        Assert.Contains("no puede quedar en 1", ex.Message);
    }

    [Fact]
    public async Task AddOperatorCharge_EstimatedRateWithoutDate_Rejected()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, _) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        await AddConfirmedLineWithChargeAsync(h.Ctx, bc, supplierA, amount: 20_000m, currency: "ARS", targetInvoiceId: bc.OriginatingInvoiceId);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.AddOperatorChargeAsync(
                bc.PublicId,
                new AddOperatorChargeRequest(
                    Kind: OperatorChargeKind.Tax,
                    CollectionMode: PenaltyCollectionMode.Retenida,
                    Amount: 1_000m,
                    Currency: "ARS",
                    EstimatedExchangeRateToClientInvoiceCurrency: 1200m,
                    EstimatedExchangeRateSource: ExchangeRateSource.Manual,
                    EstimatedExchangeRateAt: null, // falta la fecha
                    EstimatedExchangeRateJustification: "TC de hoy"),
                userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true));
        Assert.Contains("necesita su fecha", ex.Message);
    }

    [Fact]
    public async Task Emit_CrossCurrencyChargeWithExchangeRateOne_RoutesManual()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        var usdInvoice = await AddSecondActiveInvoiceAsync(h.Ctx, reserva, monId: "DOL", monCotiz: 1000m, importeTotal: 300m);
        // Cargo en ARS, factura destino en USD -> necesita conversion; con TC == 1 (default peligroso) -> Manual.
        await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierA, amount: 100m, currency: "ARS", targetInvoiceId: usdInvoice.Id, estimatedRate: 1m);

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Contains("no es válido", reloaded.DebitNoteArcaErrorMessage);
        // S4: no se persistio ningun TC definitivo (el build aborto).
        var charge = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().SingleAsync();
        Assert.Null(charge.DefinitiveExchangeRateAtNdEmission);
    }

    // ============================================================
    // F2 — TC estimado vencido (más de 48 h) al emitir -> Manual.
    // ============================================================

    [Fact]
    public async Task Emit_StaleEstimatedRate_RoutesManual()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        var usdInvoice = await AddSecondActiveInvoiceAsync(h.Ctx, reserva, monId: "DOL", monCotiz: 1000m, importeTotal: 300m);
        var line = await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierA, amount: 100m, currency: "ARS", targetInvoiceId: usdInvoice.Id, estimatedRate: 1000m);
        // Envejecer el TC estimado a 3 dias atras.
        var charge = await h.Ctx.BookingCancellationLineOperatorCharges.SingleAsync(c => c.BookingCancellationLineId == line.Id);
        charge.EstimatedExchangeRateAt = DateTime.UtcNow.AddDays(-3);
        await h.Ctx.SaveChangesAsync();

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        var reloaded = await h.Ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Contains("más de dos días", reloaded.DebitNoteArcaErrorMessage);
    }

    // ============================================================
    // S4 — multi-cargo con uno OK + uno que aborta -> NINGÚN Definitive* persistido (mutación fantasma).
    // ============================================================

    [Fact]
    public async Task Emit_MultiChargeOneAborts_NoDefinitivePersistedForAny()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var supplierB = await AddSupplierAsync(h.Ctx, "Operador B");
        var (bc, _, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        var usdInvoice = await AddSecondActiveInvoiceAsync(h.Ctx, reserva, monId: "DOL", monCotiz: 1000m, importeTotal: 300m);
        // Ambos cargos apuntan a la MISMA factura destino (resolucion pasa, count==1): el abort ocurre DENTRO del
        // foreach de conversion, DESPUES de que el cargo A ya recolecto su asignacion pendiente — ese es el caso
        // exacto que el fix two-phase (S4) protege.
        // Cargo A: cross-currency VALIDO (recolecta pending; se fijaria definitivo si el build llegara a Ready).
        var lineA = await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierA, amount: 100m, currency: "ARS", targetInvoiceId: usdInvoice.Id, estimatedRate: 1000m);
        // Cargo B (operador posterior): cross-currency con TC VENCIDO (>48h) -> aborta a Manual DENTRO del foreach.
        var lineB = await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierB, amount: 50m, currency: "ARS", targetInvoiceId: usdInvoice.Id, estimatedRate: 1000m);
        var chargeBStale = await h.Ctx.BookingCancellationLineOperatorCharges.SingleAsync(c => c.BookingCancellationLineId == lineB.Id);
        chargeBStale.EstimatedExchangeRateAt = DateTime.UtcNow.AddDays(-3);
        await h.Ctx.SaveChangesAsync();

        var dto = await h.Service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("ManualReview", dto.DebitNoteStatus);
        // El cargo A NO debe haber quedado con Definitive* persistido pese a que su conversion era valida: el
        // build aborto por el cargo B DENTRO del foreach, y la fase 2 (asignacion) nunca corrio.
        var chargeA = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking()
            .SingleAsync(c => c.BookingCancellationLineId == lineA.Id);
        Assert.Null(chargeA.DefinitiveExchangeRateAtNdEmission);
        Assert.Null(chargeA.DefinitiveExchangeRateAt);
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // S3 — PATCH target-invoice después de emitida/en vuelo la ND -> 409.
    // ============================================================

    [Fact]
    public async Task SetTargetInvoice_AfterDebitNoteInFlight_Rejected()
    {
        var h = BuildService();
        var supplierA = await AddSupplierAsync(h.Ctx, "Operador A");
        var (bc, _, reserva) = await SeedConfirmedReadyToRetryAsync(h.Ctx, supplierA);
        var secondInvoice = await AddSecondActiveInvoiceAsync(h.Ctx, reserva);
        var line = await AddConfirmedLineWithChargeAsync(
            h.Ctx, bc, supplierA, amount: 20_000m, currency: "ARS", targetInvoiceId: bc.OriginatingInvoiceId);
        var charge = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking()
            .SingleAsync(c => c.BookingCancellationLineId == line.Id);

        // La ND del BC ya salió (en vuelo).
        bc.DebitNoteInvoiceId = 12345;
        bc.DebitNoteStatus = DebitNoteStatus.Pending;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.SetOperatorChargeTargetInvoiceAsync(
                bc.PublicId, charge.PublicId,
                new SetOperatorChargeTargetInvoiceRequest(secondInvoice.PublicId),
                userId: "u", userName: "U", ct: default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR044-TARGETINVOICE-003", ex.InvariantCode);
    }

    // ============================================================
    // S2 — doble liquidación de un cargo FacturadaAparte (via SupplierService real).
    // ============================================================

    [Fact]
    public async Task SupplierPayment_DoubleLiquidationOfInvoicedCharge_RejectedThenClearedOnDelete()
    {
        var ctx = NewDbContext();
        var (_, _, charge, supplier) = await SeedChargeWithDefinitiveRateAsync(
            ctx, chargeAmount: 100m, definitiveRate: 1000m, collectionMode: PenaltyCollectionMode.FacturadaAparte);
        var service = new TravelApi.Infrastructure.Services.SupplierService(ctx);

        SupplierPaymentRequest PayRequest() => new SupplierPaymentRequest(
            Amount: 100m, Method: "Transfer", Reference: null, Notes: null, ReservaId: null,
            ServicioReservaId: null, IsAdvanceToAccount: true, Currency: "USD",
            SettlesOperatorChargePublicId: charge.PublicId);

        // Primer pago: liquida el cargo y lo marca.
        var firstPaymentPublicId = await service.AddSupplierPaymentAsync(supplier.Id, PayRequest(), default);
        var settledCharge = await ctx.BookingCancellationLineOperatorCharges.AsNoTracking().SingleAsync(c => c.Id == charge.Id);
        Assert.NotNull(settledCharge.SettledBySupplierPaymentId);

        // Segundo pago sobre el MISMO cargo -> rechazo.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddSupplierPaymentAsync(supplier.Id, PayRequest(), default));
        Assert.Contains("ya se pagó", ex.Message);

        // Al eliminar el pago que liquidaba, se limpia la marca y el cargo puede volver a pagarse.
        var firstPaymentId = await ctx.SupplierPayments.Where(p => p.PublicId == firstPaymentPublicId)
            .Select(p => p.Id).SingleAsync();
        await service.DeleteSupplierPaymentAsync(supplier.Id, firstPaymentId, default);
        var afterDelete = await ctx.BookingCancellationLineOperatorCharges.AsNoTracking().SingleAsync(c => c.Id == charge.Id);
        Assert.Null(afterDelete.SettledBySupplierPaymentId);
    }

    // ============================================================
    // K1 — ReassociateAsync registra el ajuste FX de la liquidación NUEVA y enlaza la superseded (via servicio real).
    // ============================================================

    [Fact]
    public async Task Reassociate_RegistersNewFxAdjustment_AndLinksSuperseded()
    {
        var ctx = NewDbContext();

        var bcServiceMock = new Mock<IBookingCancellationService>();
        bcServiceMock.Setup(s => s.OnAllocationVoidedAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        bcServiceMock.Setup(s => s.OnAllocationRecordedAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clientCreditMock = new Mock<IClientCreditService>();
        clientCreditMock.Setup(s => s.CreateEntryAsync(
                It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientCreditEntry());
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });

        var refundService = new OperatorRefundService(
            ctx, bcServiceMock.Object, clientCreditMock.Object, new Mock<IAuditService>().Object,
            settingsMock.Object, NullLogger<OperatorRefundService>.Instance);

        var customer = new Customer { FullName = "Cliente Reasoc", IsActive = true };
        var supplier = new Supplier { Name = "Operador Reasoc", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        FiscalSnapshot Snap() => new FiscalSnapshot
        {
            CurrencyAtEvent = "USD", AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
            SupplierTaxConditionAtEvent = "MONOTRIBUTISTA", Source = ExchangeRateSource.Manual,
            FetchedAt = DateTime.UtcNow.AddDays(-5),
        };

        async Task<(BookingCancellation Bc, BookingCancellationLineOperatorCharge Charge)> SeedBcWithChargeAsync(string numero)
        {
            var reserva = new Reserva { NumeroReserva = numero, Name = numero, PayerId = customer.Id, Balance = 0m };
            ctx.Reservas.Add(reserva);
            await ctx.SaveChangesAsync();
            var arsInvoice = new Invoice
            {
                TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 1, CAE = "cae", Resultado = "A",
                MonId = "PES", ImporteTotal = 500_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
            };
            ctx.Invoices.Add(arsInvoice);
            await ctx.SaveChangesAsync();
            var bc = new BookingCancellation
            {
                ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
                OriginatingInvoiceId = arsInvoice.Id, Reason = "Cancelacion reasoc",
                Status = BookingCancellationStatus.AwaitingOperatorRefund, FiscalSnapshot = Snap(),
            };
            ctx.BookingCancellations.Add(bc);
            await ctx.SaveChangesAsync();
            var line = new BookingCancellationLine
            {
                BookingCancellationId = bc.Id, SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel,
                ServiceId = 1, Scope = BookingCancellationLineScope.Full, Currency = "USD", RefundCap = 0m,
                PenaltyStatus = PenaltyStatus.Confirmed,
            };
            ctx.BookingCancellationLines.Add(line);
            await ctx.SaveChangesAsync();
            var charge = new BookingCancellationLineOperatorCharge
            {
                BookingCancellationLineId = line.Id, Kind = OperatorChargeKind.AdministrativeFee,
                CollectionMode = PenaltyCollectionMode.Retenida, Amount = 100m, Currency = "USD",
                TargetInvoiceId = arsInvoice.Id, DefinitiveExchangeRateAtNdEmission = 1000m,
                DefinitiveExchangeRateSource = ExchangeRateSource.Manual, DefinitiveExchangeRateAt = DateTime.UtcNow.AddDays(-1),
                ConfirmedByUserId = "u1",
            };
            ctx.BookingCancellationLineOperatorCharges.Add(charge);
            await ctx.SaveChangesAsync();
            return (bc, charge);
        }

        var (oldBc, oldCharge) = await SeedBcWithChargeAsync("R-OLD");
        var (newBc, newCharge) = await SeedBcWithChargeAsync("R-NEW");

        // Refund + allocation contra el BC VIEJO, con su ajuste FX vigente ya registrado.
        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id, ReceivedAmount = 100_000m, Currency = "USD",
            ExchangeRateAtReceipt = 1100m, ReceivedByUserId = "cashier",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();
        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id, BookingCancellationId = oldBc.Id,
            GrossAmount = 100_000m, NetAmount = 100_000m, CreatedByUserId = "cashier",
        };
        ctx.OperatorRefundAllocations.Add(allocation);
        await ctx.SaveChangesAsync();
        var trackedAllocation = await ctx.OperatorRefundAllocations.Include(a => a.Refund).SingleAsync(a => a.Id == allocation.Id);
        await TreasuryFxAdjustmentEngine.RegisterForRetainedChargesAsync(ctx, trackedAllocation, null, default);
        await ctx.SaveChangesAsync();
        var oldFx = await ctx.BookingCancellationLineTreasuryFxAdjustments.SingleAsync(a => a.OperatorChargeId == oldCharge.Id);
        Assert.False(oldFx.IsSuperseded);
        ctx.ChangeTracker.Clear();

        // Reasociar la allocation del BC viejo al BC nuevo (via servicio real).
        await refundService.ReassociateAllocationAsync(
            allocation.PublicId,
            new ReassociateAllocationRequest(newBc.PublicId, "Reasociacion por correccion de imputacion de reembolso."),
            "admin", "Admin", default);

        // El ajuste viejo quedo superseded; el nuevo se registro sobre el cargo del BC NUEVO y esta enlazado.
        var oldFxReloaded = await ctx.BookingCancellationLineTreasuryFxAdjustments.AsNoTracking()
            .SingleAsync(a => a.OperatorChargeId == oldCharge.Id);
        Assert.True(oldFxReloaded.IsSuperseded);
        var newFx = await ctx.BookingCancellationLineTreasuryFxAdjustments.AsNoTracking()
            .SingleAsync(a => a.OperatorChargeId == newCharge.Id && !a.IsSuperseded);
        Assert.Equal(newFx.Id, oldFxReloaded.SupersededByAdjustmentId);
        Assert.Equal(10_000m, newFx.DeltaAmount); // (1100-1000) x 100.
    }
}
