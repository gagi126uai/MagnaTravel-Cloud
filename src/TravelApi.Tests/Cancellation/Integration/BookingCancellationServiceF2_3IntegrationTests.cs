using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.3 Fase 2 — F2.3 integration tests (plan tactico Fase 2 §FC1.3.F2.3, 2026-05-28):
/// valida el reemplazo del fallback FC1.2 por la emision real de NC parcial en
/// <c>BookingCancellationService.OnApprovedAsync</c>.
///
/// <para><b>Que cubren estos 7 tests del path Fase 2</b>:
/// <list type="bullet">
///   <item>Flag F2 ON => llama a <c>EnqueuePartialCreditNoteAsync</c> con los Lines correctos.</item>
///   <item>Flag F2 OFF => fallback FC1.2 (NC TOTAL) con log warning, sin regresion.</item>
///   <item>Items no reintegrables => excluidos de los Lines.</item>
///   <item>Multi-alicuotas => las 2 alicuotas se preservan con prorrateo.</item>
///   <item>Sum mismatch en runtime => aborta + audit log + emit no se llama.</item>
///   <item>Idempotencia: 2 invocaciones => 2da es no-op.</item>
///   <item>Multi-payments scenario (G-F2-D) => receipts NO cascade-voided.</item>
/// </list>
/// </para>
///
/// <para><b>Por que Postgres real</b>: el doble-write FiscalLiquidation_* + JSON del
/// Metadata depende del CHECK SQL chk_BookingCancellations_fiscalliquidation_sum y del
/// CHECK chk_BookingCancellations_fiscalliquidation_consistency. InMemory no los aplica
/// y los tests pasarian falsamente.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class BookingCancellationServiceF2_3IntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public BookingCancellationServiceF2_3IntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Helpers — copian el patron de BookingCancellationServicePartialCreditNoteIntegrationTests
    // pero exponen los settings del flag F2 ON/OFF.
    // =========================================================================

    private record ServiceBundle(
        BookingCancellationService Service,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceMock,
        Mock<IAdminUserCountService> AdminCountMock,
        Mock<IOperationalFinanceSettingsService> SettingsMock,
        IApprovalRequestService ApprovalService);

    /// <summary>
    /// Arma el bundle con el calculator REAL + settings configurables.
    /// </summary>
    /// <param name="enableF2RealEmission">Si <c>true</c>, prende
    /// <c>EnablePartialCreditNoteRealEmission</c> (path Fase 2).</param>
    private ServiceBundle BuildService(
        bool enableF2RealEmission = true,
        AppDbContext? ctxIn = null)
    {
        var ctx = ctxIn ?? _fixture.CreateDbContext();

        var invoiceMock = new Mock<IInvoiceService>();
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);
        invoiceMock
            .Setup(s => s.EnqueuePartialCreditNoteAsync(
                It.IsAny<int>(), It.IsAny<PartialCreditNoteEmissionInput>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnablePartialCreditNotes = true,
                EnablePartialCreditNoteRealEmission = enableF2RealEmission,
                OnePerReservaInvoicePolicy = true,
                OperatorRefundTimeoutDays = 60,
                PartialNcAutoApprovalThreshold = 500_000m,
                PartialNcAdminReviewThreshold = 2_000_000m,
                PartialNcAccountingReviewThreshold = null,
                PartialNcDescriptionTemplate =
                    "NC parcial s/Fc {invoiceType} {invoiceNumber} (PV {pointOfSale}). " +
                    "Monto fiscal acreditado: {fiscalAmount} {currency}.",
            });

        var approvalService = new ApprovalRequestService(ctx, settingsMock.Object);
        var auditRepo = new Repository<AuditLog>(ctx);
        var auditService = new AuditService(auditRepo, NullLogger<AuditService>.Instance);
        var calculator = new FiscalLiquidationCalculator(NullLogger<FiscalLiquidationCalculator>.Instance);

        var adminCountMock = new Mock<IAdminUserCountService>();
        adminCountMock
            .Setup(a => a.CountActiveAdminsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalService, auditService,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object, calculator, adminCountMock.Object);

        return new ServiceBundle(service, ctx, invoiceMock, adminCountMock, settingsMock, approvalService);
    }

    /// <summary>
    /// Seed para los tests de F2.3: factura Hotel con items configurables.
    /// Devuelve el BC en estado ManualReviewPending listo para invocar el bridge.
    ///
    /// <para>El BC se arma manualmente (sin pasar por DraftAsync/ConfirmAsync) porque
    /// algunos tests requieren control fino del estado (multi-payments, multi-alicuotas,
    /// items no reintegrables, etc.).</para>
    /// </summary>
    private async Task<(int CustomerId, int SupplierId, int ReservaId, int InvoiceId, Guid BcPublicId, int ApprovalId)>
        SeedManualReviewPendingBcAsync(
            AppDbContext ctx,
            decimal importeTotal,
            decimal fiscalAmountToCredit,
            decimal nonRefundableAmount = 0m,
            decimal operatorPenaltyAmount = 0m,
            int tipoComprobante = 6,
            IReadOnlyList<(string Description, decimal Total, int AlicuotaId, bool IsRefundable)>? customItems = null,
            ReviewRequiredReason extraFlags = ReviewRequiredReason.AmountAboveAdminThreshold,
            // F2.3 R1 contador: parametros opcionales para forzar moneda no-ARS en la
            // semilla. Default ARS / TC=1 mantiene compatible los tests anteriores.
            string currencyAtEvent = "ARS",
            decimal exchangeRateAtEvent = 1m,
            // GAP-1 (2026-05-28): codigo ARCA con el que la FACTURA ORIGEN quedo registrada
            // ("PES" / "DOL"). Distinto del snapshot: el snapshot dice la moneda del evento
            // (ISO "ARS"/"USD"), este es lo que ARCA tiene grabado en el comprobante madre.
            // Default "PES" replica una factura registrada en pesos (caso historico).
            string originInvoiceMonId = "PES")
    {
        // FIX 2026-05-28: auto-completar penalty para que el CHECK constraint
        // chk_BookingCancellations_fiscalliquidation_sum cuadre. El invariante exige
        // OriginalInvoiceAmount = FiscalAmountToCredit + NonRefundableItemsAmount + OperatorPenaltyAmount.
        // Cuando el caller pasa solo importeTotal+fiscalAmountToCredit (defaults nonRefundable=0,
        // penalty=0), la suma NO daria importeTotal y la DB rebota. Atribuimos el delta restante
        // al penalty del operador (es el "balde" semantico mas natural del seed).
        if (operatorPenaltyAmount == 0m)
        {
            var delta = importeTotal - fiscalAmountToCredit - nonRefundableAmount;
            if (delta > 0m) operatorPenaltyAmount = delta;
        }

        var customer = new Customer
        {
            FullName = "Cliente F2.3 Test",
            TaxCondition = "Consumidor Final",
            IsActive = true,
            TaxId = "20111111111",
        };
        var supplier = new Supplier
        {
            Name = "Operador F2.3",
            IsActive = true,
            TaxCondition = "IVA_RESP_INSCRIPTO",
            InvoicingMode = SupplierInvoicingMode.TotalToCustomer,
        };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"F23-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Reserva F2.3",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotelService = new ServicioReserva
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            ProductType = ServiceTypes.Hotel,
            ServiceType = "Hotel",
            Description = "Hotel F2.3",
            DepartureDate = DateTime.UtcNow.AddDays(15),
        };
        ctx.Set<ServicioReserva>().Add(hotelService);
        await ctx.SaveChangesAsync();

        // Para multi-alicuotas se requieren items con AlicuotaIvaId distintos.
        var importeNeto = Math.Round(importeTotal / 1.21m, 2);
        var importeIva = importeTotal - importeNeto;

        var invoice = new Invoice
        {
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = 999,
            CAE = "12345678901234",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            Resultado = "A",
            ImporteTotal = importeTotal,
            ImporteNeto = importeNeto,
            ImporteIva = importeIva,
            // GAP-1: la factura origen queda registrada en ARCA con este codigo de moneda.
            // El guard GAP-1 lo compara contra el codigo derivado del snapshot.
            MonId = originInvoiceMonId,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        // Items: si el caller pasa customItems los usamos, sino armamos uno default refundable.
        if (customItems != null && customItems.Count > 0)
        {
            foreach (var ci in customItems)
            {
                var ivaRate = ci.AlicuotaId switch
                {
                    4 => 0.105m, // 10.5%
                    5 => 0.21m,  // 21%
                    _ => 0m,
                };
                var ciNeto = Math.Round(ci.Total / (1 + ivaRate), 2);
                var ciIva = ci.Total - ciNeto;
                ctx.Set<InvoiceItem>().Add(new InvoiceItem
                {
                    InvoiceId = invoice.Id,
                    Description = ci.Description,
                    Quantity = 1,
                    UnitPrice = ci.Total,
                    Total = ci.Total,
                    AlicuotaIvaId = ci.AlicuotaId,
                    ImporteIva = ciIva,
                    IsRefundable = ci.IsRefundable,
                    ItemCategory = ci.IsRefundable ? InvoiceItemCategory.Service : InvoiceItemCategory.AdministrativeFee,
                    SourceServicioReservaId = hotelService.Id,
                });
            }
        }
        else
        {
            ctx.Set<InvoiceItem>().Add(new InvoiceItem
            {
                InvoiceId = invoice.Id,
                Description = "Hotel 3 noches",
                Quantity = 1,
                UnitPrice = importeTotal,
                Total = importeTotal,
                AlicuotaIvaId = 5,
                ImporteIva = importeIva,
                IsRefundable = true,
                ItemCategory = InvoiceItemCategory.Service,
                SourceServicioReservaId = hotelService.Id,
            });
        }
        await ctx.SaveChangesAsync();

        // Approval primero (necesario para FK del BC).
        var approval = new ApprovalRequest
        {
            RequestType = ApprovalRequestType.PartialCreditNoteApproval,
            EntityType = "BookingCancellation",
            EntityId = 0,
            RequestedByUserId = "vendedor-1",
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Approved, // ya aprobado: estamos en el callback
            ResolvedByUserId = "admin-distinto",
            ResolvedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "Pendiente revision F2.3",
            Metadata = "{}",
        };
        ctx.ApprovalRequests.Add(approval);
        await ctx.SaveChangesAsync();

        // BC en ManualReviewPending con FiscalLiquidation persistido (F2.1 invariante).
        var computedAt = DateTime.UtcNow;
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.ManualReviewPending,
            Reason = "Cancelacion F2.3 manual review",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            DraftedByUserName = "Vendedor 1",
            AmountPaidAtCancellation = importeTotal,
            EstimatedRefundAmount = fiscalAmountToCredit,
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.BCRA_A3500,
                ExchangeRateAtOriginalInvoice = exchangeRateAtEvent,
                CurrencyAtEvent = currencyAtEvent,
                FetchedAt = DateTime.UtcNow,
                AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent = "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
                InvoicingModeAtEvent = SupplierInvoicingMode.TotalToCustomer,
            },
            CreditNoteKind = CreditNoteKind.PartialOnOriginal,
            ReviewRequiredReason = extraFlags,
            LiquidationComputedAt = computedAt,
            LiquidationComputedByUserId = "vendedor-1",
            LiquidationComputedByUserName = "Vendedor 1",
            PartialCreditNoteApprovalRequestId = approval.Id,
            FiscalLiquidation = new FiscalLiquidation
            {
                OriginalInvoiceAmount = importeTotal,
                CancellationAmount = importeTotal,
                OperatorPenaltyAmount = operatorPenaltyAmount,
                NonRefundableItemsAmount = nonRefundableAmount,
                FiscalAmountToCredit = fiscalAmountToCredit,
                AmountToRefundCustomer = fiscalAmountToCredit,
                FinalNetInvoiced = importeTotal - fiscalAmountToCredit,
                Currency = currencyAtEvent,
                ComputedAt = computedAt,
                ComputedByUserId = "vendedor-1",
                ComputedByUserName = "Vendedor 1",
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (customer.Id, supplier.Id, reserva.Id, invoice.Id, bc.PublicId, approval.Id);
    }

    // =========================================================================
    // Tests del path Fase 2 (F2.3)
    // =========================================================================

    /// <summary>
    /// F2.3 happy path: con flag ON, el bridge invoca el flow nuevo de NC parcial real.
    /// Verifica que <c>EnqueuePartialCreditNoteAsync</c> recibe el input correcto.
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_Fase2On_EmitsRealPartialCreditNote()
    {
        // ARRANGE
        var bundle = BuildService(enableF2RealEmission: true);
        var (_, _, _, invoiceId, bcPublicId, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000_000m,
                fiscalAmountToCredit: 700_000m);

        // ACT
        await ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
            approvalId,
            resolverUserId: "admin-distinto",
            resolverUserName: "Admin",
            resolverNotes: "Aprobado F2.3 con justificacion completa de longitud suficiente para pasar el minimo",
            CancellationToken.None);

        // ASSERT — el path Fase 2 llama al endpoint nuevo. El antiguo NO se invoca.
        bundle.InvoiceMock.Verify(
            i => i.EnqueuePartialCreditNoteAsync(
                invoiceId,
                It.Is<PartialCreditNoteEmissionInput>(input =>
                    input.FiscalAmountToCredit == 700_000m
                    && input.OriginalTotalAmount == 1_000_000m
                    && input.Currency == "ARS"
                    && input.Lines.Count >= 1),
                "admin-distinto",
                "Admin",
                It.IsAny<string?>(),
                approvalId,
                It.IsAny<CancellationToken>()),
            Times.Once);
        bundle.InvoiceMock.Verify(
            i => i.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Never);

        // BC transiciono a AwaitingFiscalConfirmation.
        var bcAfter = await bundle.Ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bcAfter.Status);
    }

    /// <summary>
    /// F2.3 con flag OFF: fallback FC1.2 (NC TOTAL) sin regresion.
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_Fase2Off_FallsBackToFc12FlowWithWarning()
    {
        var bundle = BuildService(enableF2RealEmission: false);
        var (_, _, _, _, bcPublicId, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 100_000m,
                fiscalAmountToCredit: 70_000m);

        await ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
            approvalId,
            "admin-distinto",
            "Admin",
            "Aprobado fallback FC1.2 con justificacion completa de longitud suficiente para minimo",
            CancellationToken.None);

        // Path FC1.2: se invoca EnqueueAnnulmentAsync (NC total), NO el nuevo.
        bundle.InvoiceMock.Verify(
            i => i.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), true, It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Once);
        bundle.InvoiceMock.Verify(
            i => i.EnqueuePartialCreditNoteAsync(
                It.IsAny<int>(), It.IsAny<PartialCreditNoteEmissionInput>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var bcAfter = await bundle.Ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bcAfter.Status);
    }

    /// <summary>
    /// F2.3: items no reintegrables se EXCLUYEN de los Lines del request.
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_NonRefundableItems_ExcludedFromLines()
    {
        var bundle = BuildService(enableF2RealEmission: true);

        // Factura $1.000.000: 3 items. 2 refundables ($400k + $500k) + 1 no refundable ($100k).
        // FiscalAmountToCredit = $900k (los dos refundables), penalty=0, nonRefundable=$100k.
        var customItems = new List<(string Description, decimal Total, int AlicuotaId, bool IsRefundable)>
        {
            ("Hotel noche 1", 400_000m, 5, true),
            ("Hotel noche 2", 500_000m, 5, true),
            ("Cargo de gestion", 100_000m, 5, false),
        };

        var (_, _, _, _, _, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000_000m,
                fiscalAmountToCredit: 900_000m,
                nonRefundableAmount: 100_000m,
                customItems: customItems,
                extraFlags: ReviewRequiredReason.HasNonRefundableItems | ReviewRequiredReason.AmountAboveAdminThreshold);

        PartialCreditNoteEmissionInput? captured = null;
        bundle.InvoiceMock
            .Setup(s => s.EnqueuePartialCreditNoteAsync(
                It.IsAny<int>(),
                It.IsAny<PartialCreditNoteEmissionInput>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, PartialCreditNoteEmissionInput, string, string?, string?, int, CancellationToken>(
                (id, inp, u, un, r, a, c) => captured = inp)
            .Returns(Task.CompletedTask);

        await ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
            approvalId,
            "admin-distinto",
            "Admin",
            "Aprobado: cliente cancelo pero la agencia retiene el cargo de gestion segun criterio contador",
            CancellationToken.None);

        Assert.NotNull(captured);
        // Solo 2 lines (los refundables).
        Assert.Equal(2, captured!.Lines.Count);
        Assert.DoesNotContain(captured.Lines, l => l.Description.Contains("gestion", StringComparison.OrdinalIgnoreCase));
        // La suma de las lines debe matchear el FiscalAmountToCredit.
        Assert.Equal(900_000m, captured.Lines.Sum(l => l.Total));
    }

    /// <summary>
    /// F2.3: factura multi-alicuotas (10.5% + 21%). Las 2 alicuotas se preservan
    /// con prorrateo proporcional. Una linea por alicuota.
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_MultipleAlicuotas_PreservesAll()
    {
        var bundle = BuildService(enableF2RealEmission: true);

        // Factura $1.000.000: $400k al 10.5% (alic 4) + $600k al 21% (alic 5).
        // FiscalAmountToCredit = $500k (la mitad).
        var customItems = new List<(string Description, decimal Total, int AlicuotaId, bool IsRefundable)>
        {
            ("Hotel pais limitrofe", 400_000m, 4, true),
            ("Hotel local", 600_000m, 5, true),
        };

        var (_, _, _, _, _, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000_000m,
                fiscalAmountToCredit: 500_000m,
                customItems: customItems);

        PartialCreditNoteEmissionInput? captured = null;
        bundle.InvoiceMock
            .Setup(s => s.EnqueuePartialCreditNoteAsync(
                It.IsAny<int>(),
                It.IsAny<PartialCreditNoteEmissionInput>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<int, PartialCreditNoteEmissionInput, string, string?, string?, int, CancellationToken>(
                (id, inp, u, un, r, a, c) => captured = inp)
            .Returns(Task.CompletedTask);

        await ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
            approvalId,
            "admin-distinto",
            "Admin",
            "Aprobado: factura multi-alicuotas debe preservar las dos en la NC parcial",
            CancellationToken.None);

        Assert.NotNull(captured);
        // 2 lines: una por alicuota.
        Assert.Equal(2, captured!.Lines.Count);
        Assert.Contains(captured.Lines, l => l.AlicuotaIvaId == 4);
        Assert.Contains(captured.Lines, l => l.AlicuotaIvaId == 5);
        // La suma de las lines = FiscalAmountToCredit.
        Assert.Equal(500_000m, captured.Lines.Sum(l => l.Total));
    }

    /// <summary>
    /// F2.3: si la suma de la liquidacion no cuadra (UPDATE raw rompio INV-FC1.3-005),
    /// el service detecta + log critical + audit + abort emit.
    ///
    /// <para><b>Nota</b>: en este test bypasseamos EF haciendo un UPDATE directo via
    /// raw SQL despues del seed, porque EF tracking impediria persistir un VO violatorio.
    /// El CHECK SQL de BD tambien se desactiva temporalmente — ver comentario.</para>
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_LiquidationSumMismatch_AbortsEmission()
    {
        var bundle = BuildService(enableF2RealEmission: true);
        var (_, _, _, _, bcPublicId, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000_000m,
                fiscalAmountToCredit: 700_000m);

        try
        {
            // Romper la liquidacion via UPDATE raw, bypasseando EF + CHECK temporalmente.
            // Patron: drop check -> update -> re-add en el finally.
            // Esto simula un actor malicioso o un bug que rompio la coherencia.
            await bundle.Ctx.Database.ExecuteSqlRawAsync(
                @"ALTER TABLE ""BookingCancellations"" DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalliquidation_sum;");
            await bundle.Ctx.Database.ExecuteSqlRawAsync(
                @"UPDATE ""BookingCancellations"" SET ""FiscalLiquidation_FiscalAmountToCredit"" = 999999999;");

            // FIX 2026-05-28: el UPDATE raw cambio el xmin (concurrency token) de la
            // fila. EF tiene el BC trackeado con el xmin viejo, asi que cuando el
            // service mute el BC y persista (via AuditService.SaveChanges interno) va
            // a hacer UPDATE ... WHERE xmin=<viejo> -> 0 filas afectadas ->
            // DbUpdateConcurrencyException. Limpiamos el tracking aca para que cuando
            // el service recargue el BC lea el xmin actualizado.
            bundle.Ctx.ChangeTracker.Clear();

            // ACT + ASSERT: debe tirar INV-FC1.3-005.
            var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
                ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
                    approvalId,
                    "admin-distinto",
                    "Admin",
                    "Aprobado: pero la liquidacion esta corrupta, deberia abortar emision real F2.3",
                    CancellationToken.None));

            Assert.Equal("INV-FC1.3-005", ex.InvariantCode);

            // EnqueuePartialCreditNoteAsync NO debe haber sido llamado.
            bundle.InvoiceMock.Verify(
                i => i.EnqueuePartialCreditNoteAsync(
                    It.IsAny<int>(), It.IsAny<PartialCreditNoteEmissionInput>(),
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<int>(), It.IsAny<CancellationToken>()),
                Times.Never);

            // Audit log con la accion nueva.
            var audit = await bundle.Ctx.AuditLogs.AsNoTracking()
                .Where(a => a.Action == "PartialNcEmissionAborted_SumMismatch")
                .FirstOrDefaultAsync();
            Assert.NotNull(audit);

            // MENOR 1 backend reviewer (2026-05-28): documentar el dead-end manual.
            // El BC quedo en ManualReviewApproved (el SaveChanges implicito del
            // AuditService al loguear el manual review approved persistio la transicion)
            // pero la emision real abortarse no consumio el approval. La operadora
            // tiene que intervenir manualmente — el flag NO desconsuma el approval
            // ni revierte el BC, porque eso podria habilitar segundas pasadas con la
            // misma liquidacion corrupta.
            var bcAfter = await bundle.Ctx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.ManualReviewApproved, bcAfter.Status);
            Assert.NotNull(bcAfter.ManualReviewerUserId);
            // El approval queda Approved pero NO Consumed (dead-end manual intencional):
            // ConsumedAt sigue null porque MarkConsumedAsync se ejecuta DESPUES de
            // EnqueuePartialCreditNoteAsync en EmitRealPartialCreditNoteAsync.
            var approval = await bundle.Ctx.ApprovalRequests.AsNoTracking()
                .FirstAsync(a => a.Id == approvalId);
            Assert.Null(approval.ConsumedAt);
        }
        finally
        {
            // Restaurar el CHECK con la MISMA definicion de la migracion Fase2_M1.
            // OJO: la migracion usa "<= 0.01" (no "< 0.01") y envuelve cada componente
            // con COALESCE(..., 0). Sin esto, si este test corre primero (xUnit no
            // garantiza orden), los tests siguientes pierden la proteccion del CHECK
            // y un bug aguas arriba podria pasar sin ser detectado.
            //
            // FIX 2026-05-28: usamos NOT VALID porque el UPDATE raw del ACT dejo la
            // fila violando el constraint. Sin NOT VALID, PostgreSQL rechaza el
            // ADD CONSTRAINT con error 23514 ("is violated by some row").
            // NOT VALID re-instala el constraint para PROXIMOS inserts/updates pero
            // no valida las filas existentes. El siguiente test corre ResetDatabaseAsync
            // (TRUNCATE CASCADE) que elimina la fila violatoria, y a partir de ahi el
            // constraint queda efectivo al 100%.
            await bundle.Ctx.Database.ExecuteSqlRawAsync(@"
                ALTER TABLE ""BookingCancellations""
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalliquidation_sum;
                ALTER TABLE ""BookingCancellations""
                  ADD CONSTRAINT chk_BookingCancellations_fiscalliquidation_sum
                  CHECK (
                    ""FiscalLiquidation_FiscalAmountToCredit"" IS NULL
                    OR ABS(
                         COALESCE(""FiscalLiquidation_FiscalAmountToCredit"", 0)
                         + COALESCE(""FiscalLiquidation_NonRefundableItemsAmount"", 0)
                         + COALESCE(""FiscalLiquidation_OperatorPenaltyAmount"", 0)
                         - COALESCE(""FiscalLiquidation_OriginalInvoiceAmount"", 0)
                       ) <= 0.01
                  )
                  NOT VALID;
            ");
        }
    }

    /// <summary>
    /// F2.3: idempotencia. 2 invocaciones del bridge sobre el mismo approval =>
    /// la 2da es no-op (el BC ya no esta en ManualReviewPending).
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_IdempotenceTwoCallsSecondNoop()
    {
        var bundle = BuildService(enableF2RealEmission: true);
        var (_, _, _, _, _, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000_000m,
                fiscalAmountToCredit: 700_000m);

        // Primera invocacion: deberia hacer todo el flow.
        await ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
            approvalId,
            "admin-distinto",
            "Admin",
            "Aprobado primera invocacion con justificacion suficientemente larga para superar minimo",
            CancellationToken.None);

        // Segunda invocacion: debe ser no-op (el BC ya no esta en ManualReviewPending).
        await ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
            approvalId,
            "admin-distinto",
            "Admin",
            "Aprobado segunda invocacion debe ser no-op idempotente segun ADR-009 §2.8.3",
            CancellationToken.None);

        // EnqueuePartialCreditNoteAsync se invoco UNA SOLA vez.
        bundle.InvoiceMock.Verify(
            i => i.EnqueuePartialCreditNoteAsync(
                It.IsAny<int>(), It.IsAny<PartialCreditNoteEmissionInput>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// F2.3 + cascade integrado (RH-005 + G-F2-D end-to-end): factura $1.000 + 3 payments
    /// ($300+$300+$400) con 3 receipts vivos + NC parcial $250. Despues de aprobar:
    /// <list type="bullet">
    ///   <item>EnqueuePartialCreditNoteAsync se invoca (mock).</item>
    ///   <item>BC pasa a AwaitingFiscalConfirmation.</item>
    ///   <item>El cascade NO se ejecuta aca porque no llega a aplicarse (mock no procesa AFIP).</item>
    /// </list>
    /// El test de cascade real (AfipService) vive en AfipServicePartialCreditNoteReversalTests.
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_Fase2_PartialNc_MultiplePaymentsScenario_PreservesReceipts()
    {
        var bundle = BuildService(enableF2RealEmission: true);
        var (_, _, reservaId, invoiceId, _, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000m,
                fiscalAmountToCredit: 250m);

        // Crear 3 payments con receipts vivos (Issued) atados al payment.
        var p1 = new Payment
        {
            ReservaId = reservaId, Amount = 300m, PaidAt = DateTime.UtcNow.AddDays(-5),
            Method = "Transfer", Status = "Paid",
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true,
            RelatedInvoiceId = invoiceId,
        };
        var p2 = new Payment
        {
            ReservaId = reservaId, Amount = 300m, PaidAt = DateTime.UtcNow.AddDays(-3),
            Method = "Transfer", Status = "Paid",
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true,
            RelatedInvoiceId = invoiceId,
        };
        var p3 = new Payment
        {
            ReservaId = reservaId, Amount = 400m, PaidAt = DateTime.UtcNow.AddDays(-1),
            Method = "Transfer", Status = "Paid",
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true,
            RelatedInvoiceId = invoiceId,
        };
        bundle.Ctx.Payments.AddRange(p1, p2, p3);
        await bundle.Ctx.SaveChangesAsync();

        bundle.Ctx.PaymentReceipts.AddRange(
            new PaymentReceipt
            {
                PaymentId = p1.Id, ReservaId = reservaId, ReceiptNumber = "R1",
                Amount = 300m, Status = PaymentReceiptStatuses.Issued,
                IssuedAt = DateTime.UtcNow,
            },
            new PaymentReceipt
            {
                PaymentId = p2.Id, ReservaId = reservaId, ReceiptNumber = "R2",
                Amount = 300m, Status = PaymentReceiptStatuses.Issued,
                IssuedAt = DateTime.UtcNow,
            },
            new PaymentReceipt
            {
                PaymentId = p3.Id, ReservaId = reservaId, ReceiptNumber = "R3",
                Amount = 400m, Status = PaymentReceiptStatuses.Issued,
                IssuedAt = DateTime.UtcNow,
            });
        await bundle.Ctx.SaveChangesAsync();

        // ACT
        await ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
            approvalId,
            "admin-distinto",
            "Admin",
            "Aprobado: factura $1000 pagada en 3 cuotas, NC parcial $250 sin cascade automatico de receipts",
            CancellationToken.None);

        // ASSERT — el flow F2.3 corrio correctamente.
        bundle.InvoiceMock.Verify(
            i => i.EnqueuePartialCreditNoteAsync(
                invoiceId,
                It.Is<PartialCreditNoteEmissionInput>(input => input.FiscalAmountToCredit == 250m),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                approvalId, It.IsAny<CancellationToken>()),
            Times.Once);

        // Los 3 receipts originales siguen Issued (el cascade no se invoco aca — depende
        // del AfipService cuando AFIP confirme la NC, ver AfipServicePartialCreditNoteReversalTests).
        var receipts = await bundle.Ctx.PaymentReceipts.AsNoTracking().ToListAsync();
        Assert.Equal(3, receipts.Count);
        Assert.All(receipts, r => Assert.Equal(PaymentReceiptStatuses.Issued, r.Status));
    }

    /// <summary>
    /// FC1.3.F2.5 (multimoneda, 2026-05-28): una factura origen en USD ahora SI emite NC parcial
    /// real. Antes (F2.3) el guard abortaba todo lo que no fuera ARS porque el XML SOAP iba en
    /// pesos hardcoded. F2.5 cerro ese gap: el envelope ya interpola moneda/cotizacion reales y
    /// el guard solo rechaza monedas que el <c>ArcaCurrencyMapper</c> no sabe mapear. USD->DOL
    /// esta soportado, asi que el flujo pasa el guard y llega a la emision.
    ///
    /// <para>El service debe:</para>
    ///   - PASAR el guard de moneda (USD esta en el mapeo ARCA);
    ///   - llamar a <c>EnqueuePartialCreditNoteAsync</c> con <c>Currency == "USD"</c> y el TC del
    ///     snapshot (1000) tal cual (la NC va en la misma moneda/cotizacion que la factura origen);
    ///   - transicionar el BC a AwaitingFiscalConfirmation;
    ///   - NO persistir el audit de aborto por moneda.
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_Fase2_PartialNc_CurrencyUsd_EmitsRealPartialCreditNote()
    {
        // ARRANGE: BC con FiscalSnapshot.CurrencyAtEvent = "USD" y un TC realista del snapshot.
        var bundle = BuildService(enableF2RealEmission: true);
        var (_, _, _, invoiceId, bcPublicId, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000m,
                fiscalAmountToCredit: 700m,
                currencyAtEvent: "USD",
                exchangeRateAtEvent: 1000m,
                // GAP-1: factura USD CORRECTA post-F2.5 => registrada en ARCA como "DOL".
                // El snapshot "USD"->"DOL" coincide con el origen, asi que el guard GAP-1
                // no dispara y el flujo llega normalmente a la emision.
                originInvoiceMonId: "DOL");

        // ACT: ya NO debe tirar — USD es una moneda soportada por el mapeo ARCA.
        await ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
            approvalId,
            "admin-distinto",
            "Admin",
            "Aprobado: factura USD ahora se emite con NC parcial multimoneda (F2.5 online)",
            CancellationToken.None);

        // ASSERT: la NC parcial real se encolo con la moneda y cotizacion del snapshot.
        bundle.InvoiceMock.Verify(
            i => i.EnqueuePartialCreditNoteAsync(
                invoiceId,
                It.Is<PartialCreditNoteEmissionInput>(input =>
                    input.FiscalAmountToCredit == 700m
                    && input.OriginalTotalAmount == 1_000m
                    && input.Currency == "USD"
                    && input.ExchangeRateAtOriginalInvoice == 1000m
                    && input.Lines.Count >= 1),
                "admin-distinto",
                "Admin",
                It.IsAny<string?>(),
                approvalId,
                It.IsAny<CancellationToken>()),
            Times.Once);

        // El BC transiciono a AwaitingFiscalConfirmation (path de emision, no dead-end manual).
        var bcAfter = await bundle.Ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bcAfter.Status);

        // NO debe haberse persistido el audit de aborto por moneda no soportada.
        var abortAudit = await bundle.Ctx.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "PartialNcAborted_UnsupportedCurrency")
            .FirstOrDefaultAsync();
        Assert.Null(abortAudit);
    }

    /// <summary>
    /// FC1.3.F2.5 (multimoneda, 2026-05-28): una moneda que el <c>ArcaCurrencyMapper</c> NO sabe
    /// mapear (ej. EUR, todavia no homologada contra ARCA) sigue abortando TEMPRANO, antes de
    /// transicionar el estado del BC. Esto evita encolar una NC que el job de emision marcaria
    /// Failed igual: mejor que el operador vea el rechazo en el acto y trate el caso a mano.
    ///
    /// <para>El service debe:</para>
    ///   - loguear critical;
    ///   - persistir audit "PartialNcAborted_UnsupportedCurrency";
    ///   - NO llamar a EnqueuePartialCreditNoteAsync;
    ///   - tirar BusinessInvariantViolationException con mensaje que mencione la moneda;
    ///   - dejar el BC en ManualReviewApproved (el audit del paso previo gatillo SaveChanges).
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_Fase2_PartialNc_UnsupportedCurrency_AbortsEarly()
    {
        // ARRANGE: BC con una moneda NO soportada por el mapeo ARCA (EUR).
        var bundle = BuildService(enableF2RealEmission: true);
        var (_, _, _, _, bcPublicId, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000m,
                fiscalAmountToCredit: 700m,
                currencyAtEvent: "EUR",
                exchangeRateAtEvent: 1100m);

        // ACT + ASSERT: debe tirar BusinessInvariantViolationException por moneda no soportada.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
                approvalId,
                "admin-distinto",
                "Admin",
                "Aprobado: factura EUR deberia bloquearse porque no esta en el mapeo ARCA",
                CancellationToken.None));

        Assert.Contains("EUR", ex.Message);

        // El audit nuevo (renombrado en F2.5) debe estar persistido.
        var audit = await bundle.Ctx.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "PartialNcAborted_UnsupportedCurrency")
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);

        // EnqueuePartialCreditNoteAsync NO debe haberse invocado.
        bundle.InvoiceMock.Verify(
            i => i.EnqueuePartialCreditNoteAsync(
                It.IsAny<int>(), It.IsAny<PartialCreditNoteEmissionInput>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // El BC queda en ManualReviewApproved (dead-end manual): el audit del paso previo
        // de OnApprovedAsync gatillo SaveChanges, por eso la transicion quedo persistida
        // aun cuando la emision real aborto despues.
        var bcAfter = await bundle.Ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.ManualReviewApproved, bcAfter.Status);
        Assert.NotNull(bcAfter.ManualReviewerUserId);

        // Approval queda Approved pero NO Consumed (MarkConsumedAsync vive despues del
        // throw dentro de EmitRealPartialCreditNoteAsync, asi que nunca corrio).
        var approval = await bundle.Ctx.ApprovalRequests.AsNoTracking()
            .FirstAsync(a => a.Id == approvalId);
        Assert.Null(approval.ConsumedAt);
    }

    /// <summary>
    /// FIX M-1 (revision backend+contable, 2026-05-28): una moneda extranjera SOPORTADA (USD) pero
    /// con tipo de cambio del snapshot INCOHERENTE (0, por dato cargado via SQL crudo / backfill /
    /// path que no poblo el TC) debe abortar TERMINAL — nunca emitir una NC en DOL valuada como
    /// pesos (un dolar a cotizacion 0 o 1).
    ///
    /// <para>El service debe: NO llamar a EnqueuePartialCreditNoteAsync, persistir audit
    /// "PartialNcAborted_IncoherentRate", y tirar BusinessInvariantViolationException.</para>
    /// </summary>
    [Fact]
    public async Task OnApprovedAsync_Fase2_PartialNc_ForeignCurrencyZeroRate_AbortsTerminal()
    {
        // ARRANGE: USD (moneda soportada) PERO con TC del snapshot en 0 (incoherente).
        var bundle = BuildService(enableF2RealEmission: true);
        var (_, _, _, _, bcPublicId, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000m,
                fiscalAmountToCredit: 700m,
                currencyAtEvent: "USD",
                exchangeRateAtEvent: 0m); // TC incoherente para moneda extranjera

        // ACT + ASSERT: aborta terminal por cotizacion incoherente.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
                approvalId,
                "admin-distinto",
                "Admin",
                "Aprobado: USD con TC 0 debe bloquearse para no valuar un dolar como un peso",
                CancellationToken.None));

        Assert.Contains("USD", ex.Message);

        // Audit del aborto por cotizacion incoherente.
        var audit = await bundle.Ctx.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "PartialNcAborted_IncoherentRate")
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);

        // NO se encolo ninguna NC.
        bundle.InvoiceMock.Verify(
            i => i.EnqueuePartialCreditNoteAsync(
                It.IsAny<int>(), It.IsAny<PartialCreditNoteEmissionInput>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// GAP-1 (defense-in-depth, revision 2026-05-28): el caso legacy. Una factura en USD emitida
    /// ANTES de F2.5 quedo registrada en ARCA en PESOS (<c>OriginatingInvoice.MonId = "PES"</c>)
    /// aunque su snapshot fiscal diga <c>CurrencyAtEvent = "USD"</c>. Con el flag prendido, sin el
    /// guard GAP-1, el emisor armaria una NC en DOL asociada a una factura registrada en PES =
    /// desfasaje fiscal NC != comprobante origen. El guard lo frena ANTES de emitir.
    ///
    /// <para>El service debe:</para>
    ///   - PASAR el guard de moneda (USD soportada) y el de TC (1000 coherente);
    ///   - ABORTAR en el guard GAP-1 con audit "PartialNcAborted_CurrencyMismatchVsOrigin";
    ///   - NO llamar a EnqueuePartialCreditNoteAsync;
    ///   - dejar el BC en ManualReviewApproved (tratamiento manual), no transicionar a emision.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task OnApprovedAsync_Fase2_PartialNc_CurrencyMismatchVsOrigin_AbortsManual()
    {
        // ARRANGE: snapshot USD (TC sano = 1000) pero factura origen registrada en ARCA como PES
        // (caso factura USD legacy pre-F2.5).
        var bundle = BuildService(enableF2RealEmission: true);
        var (_, _, _, _, bcPublicId, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000m,
                fiscalAmountToCredit: 700m,
                currencyAtEvent: "USD",
                exchangeRateAtEvent: 1000m,
                originInvoiceMonId: "PES"); // <-- desfasaje: snapshot USD pero factura en PES

        // ACT + ASSERT: debe abortar por mismatch de moneda NC vs origen.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
                approvalId,
                "admin-distinto",
                "Admin",
                "Aprobado: factura USD legacy registrada en PES debe bloquearse para no desfasar la NC",
                CancellationToken.None));

        // El mensaje menciona los dos codigos ARCA (DOL derivado del snapshot vs PES del origen).
        Assert.Contains("DOL", ex.Message);
        Assert.Contains("PES", ex.Message);

        // Audit del aborto GAP-1.
        var audit = await bundle.Ctx.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "PartialNcAborted_CurrencyMismatchVsOrigin")
            .FirstOrDefaultAsync();
        Assert.NotNull(audit);

        // NO se encolo ninguna NC.
        bundle.InvoiceMock.Verify(
            i => i.EnqueuePartialCreditNoteAsync(
                It.IsAny<int>(), It.IsAny<PartialCreditNoteEmissionInput>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // El BC queda en ManualReviewApproved (dead-end manual): el audit del paso previo
        // de OnApprovedAsync gatillo SaveChanges, por eso la transicion previa quedo persistida
        // aun cuando la emision real aborto en el guard GAP-1.
        var bcAfter = await bundle.Ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.ManualReviewApproved, bcAfter.Status);

        // Approval queda Approved pero NO Consumed (MarkConsumedAsync vive despues del throw).
        var approval = await bundle.Ctx.ApprovalRequests.AsNoTracking()
            .FirstAsync(a => a.Id == approvalId);
        Assert.Null(approval.ConsumedAt);
    }

    /// <summary>
    /// GAP-1 caso feliz: factura USD CORRECTA post-F2.5 (registrada en ARCA como "DOL") +
    /// snapshot "USD" (-> "DOL"). Los dos codigos coinciden => el guard GAP-1 NO dispara y la NC
    /// parcial real se emite normal. Garantiza que el guard SOLO frena el caso incoherente y no
    /// rompe el flujo multimoneda legitimo.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task OnApprovedAsync_Fase2_PartialNc_CurrencyMatchesOrigin_EmitsNormally()
    {
        // ARRANGE: snapshot USD + factura origen "DOL" (post-F2.5, coherente).
        var bundle = BuildService(enableF2RealEmission: true);
        var (_, _, _, invoiceId, bcPublicId, approvalId) =
            await SeedManualReviewPendingBcAsync(
                ctx: bundle.Ctx,
                importeTotal: 1_000m,
                fiscalAmountToCredit: 700m,
                currencyAtEvent: "USD",
                exchangeRateAtEvent: 1000m,
                originInvoiceMonId: "DOL"); // coincide con snapshot USD->DOL

        // ACT: el guard GAP-1 no dispara; emite normal.
        await ((IPartialCreditNoteApprovalBridge)bundle.Service).OnApprovedAsync(
            approvalId,
            "admin-distinto",
            "Admin",
            "Aprobado: factura USD post-F2.5 registrada en DOL coincide con el snapshot, emite normal",
            CancellationToken.None);

        // ASSERT: la NC parcial real se encolo con la moneda del snapshot.
        bundle.InvoiceMock.Verify(
            i => i.EnqueuePartialCreditNoteAsync(
                invoiceId,
                It.Is<PartialCreditNoteEmissionInput>(input =>
                    input.FiscalAmountToCredit == 700m
                    && input.Currency == "USD"
                    && input.ExchangeRateAtOriginalInvoice == 1000m),
                "admin-distinto",
                "Admin",
                It.IsAny<string?>(),
                approvalId,
                It.IsAny<CancellationToken>()),
            Times.Once);

        // BC transiciono a emision (no dead-end manual).
        var bcAfter = await bundle.Ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bcAfter.Status);

        // NO se persistio el audit de aborto GAP-1.
        var abortAudit = await bundle.Ctx.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "PartialNcAborted_CurrencyMismatchVsOrigin")
            .FirstOrDefaultAsync();
        Assert.Null(abortAudit);
    }

}
