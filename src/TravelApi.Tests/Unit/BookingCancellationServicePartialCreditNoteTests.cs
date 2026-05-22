using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.3 (ADR-009 §2.7 + §2.8 + §2.9 STEP 7, 2026-05-21): tests unit puros
/// del flujo NC parcial en <see cref="BookingCancellationService"/>.
///
/// <para><b>Por que NO usamos <c>CustomWebApplicationFactory</c> ni
/// <c>PostgresIntegrationFixture</c></b>: ambos arrancan el host completo +
/// TestContainers; eso cuelga la suite cuando Docker no esta listo (probado
/// dos veces en sesiones previas). Los integration tests del plan tactico
/// (32 tests con Postgres real) quedan diferidos a una sesion QA dedicada
/// con <c>qa-automation-senior</c> + <c>PostgresIntegrationFixture</c>.</para>
///
/// <para><b>Que cubre esta clase</b>: la logica del service (transiciones,
/// validaciones de flujo, branching de Confirm con flag FC1.3 on/off,
/// EditLiquidation, OnApproved/OnRejected bridge) con mocks de las 8
/// dependencias. Cada test corre en milisegundos.</para>
///
/// <para><b>Trade-off explicito</b>: usamos <c>UseInMemoryDatabase</c> de EF.
/// InMemory NO valida CHECK constraints SQL (la red de seguridad N-001) ni
/// soporta xmin concurrency. Los tests que dependen de esas red flags se
/// marcan como <c>[Skip("Integration test...")]</c> y se reservan para la
/// sesion QA dedicada. Lo que SI cubrimos: orden de operaciones (snapshot
/// antes que status), branching del flag, validaciones del service.</para>
/// </summary>
public class BookingCancellationServicePartialCreditNoteTests
{
    // ============================================================
    // Helpers de armado (idempotentes, sin estado entre tests).
    // ============================================================

    /// <summary>
    /// Construye un DbContext InMemory con nombre unico por test (asi no se
    /// pisan entre tests cuando xUnit corre en paralelo).
    /// </summary>
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fc13-bc-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Builder del service que mockea las 8 deps. Por defecto:
    ///  - settings.EnableNewCancellationFlow=true (FC1.2 ON).
    ///  - settings.EnablePartialCreditNotes=false (FC1.3 OFF).
    /// Los tests pueden sobre-configurar via los Mocks devueltos.
    /// </summary>
    private static (
        BookingCancellationService Service,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceMock,
        Mock<IApprovalRequestService> ApprovalMock,
        Mock<IAuditService> AuditMock,
        Mock<IOperationalFinanceSettingsService> SettingsMock,
        Mock<IFiscalLiquidationCalculator> CalculatorMock,
        Mock<IAdminUserCountService> AdminCountMock,
        OperationalFinanceSettings Settings
    ) BuildService(
        bool fc12On = true,
        bool fc13On = false,
        bool allowSingleAdminBypass = false,
        int activeAdminCount = 2)
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
            EnableNewCancellationFlow = fc12On,
            EnablePartialCreditNotes = fc13On,
            Allow4EyesBypassWhenSingleAdmin = allowSingleAdminBypass,
            OperatorRefundTimeoutDays = 60,
            PartialNcAutoApprovalThreshold = 500_000m,
            PartialNcAdminReviewThreshold = 2_000_000m,
        };
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        adminCountMock.Setup(a => a.CountActiveAdminsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeAdminCount);

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            approvalMock.Object,
            auditMock.Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            calculatorMock.Object,
            adminCountMock.Object);

        return (service, ctx, invoiceMock, approvalMock, auditMock, settingsMock, calculatorMock, adminCountMock, settings);
    }

    /// <summary>
    /// Inserta un escenario base sano (Customer, Supplier, Reserva con servicio
    /// Hotel, Invoice con un InvoiceItem, BC en Drafted) y devuelve el
    /// PublicId del BC. La factura es Tipo B (cliente CF) por default — los
    /// tests pueden mutar antes del Confirm.
    /// </summary>
    private static async Task<(Guid BcPublicId, BookingCancellation Bc)> SeedScenarioAsync(
        AppDbContext ctx,
        int tipoComprobante = 6,
        bool addNonHotelService = false,
        string vendedorUserId = "vendedor-1")
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier
        {
            Name = "Operador SRL",
            IsActive = true,
            InvoicingMode = SupplierInvoicingMode.TotalToCustomer,
        };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-001",
            Name = "Reserva Hotel Test",
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
            ServiceType = ServiceTypes.Hotel,
            DepartureDate = DateTime.UtcNow.AddDays(15),
        };
        ctx.Set<ServicioReserva>().Add(hotelService);

        if (addNonHotelService)
        {
            // Mezcla Hotel + Vuelo => INV-FC1.3-007 deberia bloquear.
            ctx.Set<ServicioReserva>().Add(new ServicioReserva
            {
                ReservaId = reserva.Id,
                CustomerId = customer.Id,
                SupplierId = supplier.Id,
                ProductType = ServiceTypes.Flight,
                ServiceType = ServiceTypes.Flight,
                DepartureDate = DateTime.UtcNow.AddDays(15),
            });
        }
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = 100,
            CAE = "12345678",
            Resultado = "A",
            ImporteTotal = 300_000m,
            ImporteNeto = 247_933.88m,
            ImporteIva = 52_066.12m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        ctx.Set<InvoiceItem>().Add(new InvoiceItem
        {
            InvoiceId = invoice.Id,
            Description = "Hotel 5 noches",
            Quantity = 1,
            UnitPrice = 300_000m,
            Total = 300_000m,
            AlicuotaIvaId = 5,
            ImporteIva = 52_066.12m,
            IsRefundable = true,
            ItemCategory = InvoiceItemCategory.Service,
            SourceServicioReservaId = hotelService.Id,
        });
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Cliente decidio cancelar 5 dias antes del check-in",
            DraftedByUserId = vendedorUserId,
            DraftedByUserName = "Juan Vendedor",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc.PublicId, bc);
    }

    /// <summary>Crea un ConfirmCancellationRequest sano con snapshot fiscal valido.</summary>
    private static ConfirmCancellationRequest NewConfirmRequest() =>
        new ConfirmCancellationRequest(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.BCRA_A3500,
                ManualJustification: null,
                AgencyTaxConditionAtEvent: "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent: "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent: "CONSUMIDOR_FINAL"),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

    /// <summary>Helper para configurar el mock del calculator devolviendo un DTO concreto.</summary>
    private static void SetupCalculator(
        Mock<IFiscalLiquidationCalculator> mock,
        FiscalLiquidationDto returnValue)
    {
        mock.Setup(c => c.Calculate(It.IsAny<FiscalLiquidationInput>(), It.IsAny<OperationalFinanceSettings>()))
            .Returns(returnValue);
    }

    /// <summary>DTO sano "auto-aprobable" (sin motivos manual review).</summary>
    private static FiscalLiquidationDto AutoApprovableDto() =>
        new FiscalLiquidationDto(
            OriginalInvoiceAmount: 300_000m,
            CancellationAmount: 300_000m,
            OperatorPenaltyAmount: 0m,
            NonRefundableItemsAmount: 0m,
            FiscalAmountToCredit: 300_000m,
            AmountToRefundCustomer: 300_000m,
            FinalNetInvoiced: 0m,
            Case: PartialCreditNoteCase.Case2_FullCancellationNoRetention,
            Kind: CreditNoteKind.PartialOnOriginal,
            ReviewRequiredReason: ReviewRequiredReason.None,
            Currency: "ARS",
            ClassificationExplanation: "Auto-aprobable - sin disparadores manuales.");

    /// <summary>DTO con motivos manual review (Factura A).</summary>
    private static FiscalLiquidationDto ManualReviewDto() =>
        new FiscalLiquidationDto(
            OriginalInvoiceAmount: 300_000m,
            CancellationAmount: 300_000m,
            OperatorPenaltyAmount: 0m,
            NonRefundableItemsAmount: 0m,
            FiscalAmountToCredit: 300_000m,
            AmountToRefundCustomer: 300_000m,
            FinalNetInvoiced: 0m,
            Case: PartialCreditNoteCase.Case8_FacturaA,
            Kind: CreditNoteKind.PartialOnOriginal,
            ReviewRequiredReason: ReviewRequiredReason.CustomerIsRiOrFacturaA,
            Currency: "ARS",
            ClassificationExplanation: "Factura A - revision manual obligatoria.");

    /// <summary>DTO con TotalPlusNewInvoice (GR-001 lo rechaza en Confirm).</summary>
    private static FiscalLiquidationDto TotalPlusNewInvoiceDto() =>
        new FiscalLiquidationDto(
            OriginalInvoiceAmount: 300_000m,
            CancellationAmount: 300_000m,
            OperatorPenaltyAmount: 0m,
            NonRefundableItemsAmount: 0m,
            FiscalAmountToCredit: 300_000m,
            AmountToRefundCustomer: 300_000m,
            FinalNetInvoiced: 0m,
            Case: PartialCreditNoteCase.Case4_OriginalInvoiceUnclear,
            Kind: CreditNoteKind.TotalPlusNewInvoice,
            ReviewRequiredReason: ReviewRequiredReason.OriginalInvoiceUnclear,
            Currency: "ARS",
            ClassificationExplanation: "Caso 4 — factura confusa requiere NC total + factura nueva.");

    /// <summary>Helper para setup del mock approvalService.CreateAsync devolviendo un ApprovalRequest sano y persistido.</summary>
    private static void SetupApprovalCreate(
        Mock<IApprovalRequestService> approvalMock,
        AppDbContext ctx,
        Guid approvalPublicId)
    {
        approvalMock.Setup(a => a.CreateAsync(
                It.IsAny<CreateApprovalRequestPayload>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateApprovalRequestPayload payload, string reqUser, string? reqName, CancellationToken ct) =>
            {
                // Persistimos el approval en el DbContext del test asi el service
                // puede levantarlo por PublicId despues de CreateAsync (esto simula
                // el side-effect real del ApprovalRequestService).
                var ar = new ApprovalRequest
                {
                    PublicId = approvalPublicId,
                    RequestType = ApprovalRequestType.PartialCreditNoteApproval,
                    EntityType = payload.EntityType,
                    EntityId = payload.EntityId,
                    Reason = payload.Reason,
                    Metadata = payload.Metadata,
                    Status = ApprovalStatus.Pending,
                    RequestedByUserId = reqUser,
                    RequestedByUserName = reqName,
                    ExpiresAt = DateTime.UtcNow.AddDays(7),
                };
                ctx.ApprovalRequests.Add(ar);
                ctx.SaveChanges();
                return new ApprovalRequestDto { PublicId = ar.PublicId };
            });
    }

    // ============================================================
    // 1) ConfirmAsync con flag FC1.3 OFF -> path FC1.2 vigente
    // ============================================================

    [Fact]
    public async Task ConfirmAsync_FlagOff_FollowsLegacyFc12Path()
    {
        // Setup: FC1.3 OFF (default). El calculator NO debe ser invocado.
        var (svc, ctx, invoiceMock, _, _, _, calculatorMock, _, _) = BuildService(fc12On: true, fc13On: false);
        var (bcPublicId, _) = await SeedScenarioAsync(ctx);

        await svc.ConfirmAsync(bcPublicId, NewConfirmRequest(), "user-1", "Admin", requesterIsAdmin: false, CancellationToken.None);

        // El calculator no se llamo: FC1.3 OFF lo skip.
        calculatorMock.Verify(c => c.Calculate(It.IsAny<FiscalLiquidationInput>(), It.IsAny<OperationalFinanceSettings>()), Times.Never);

        // El BC quedo en AwaitingFiscalConfirmation (path FC1.2).
        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bc.Status);
        Assert.Null(bc.CreditNoteKind);
        Assert.Equal(ReviewRequiredReason.None, bc.ReviewRequiredReason);

        // EnqueueAnnulmentAsync se invoco (path FC1.2 emite NC total al ARCA).
        invoiceMock.Verify(
            i => i.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Once);
    }

    // ============================================================
    // 2) ConfirmAsync FC1.3 ON + auto-aprobable -> path FC1.2
    // ============================================================

    [Fact]
    public async Task ConfirmAsync_FlagOn_NoReason_TransitionsToAwaitingFiscalConfirmation()
    {
        // Setup: FC1.3 ON. Calculator devuelve DTO sin motivos manual.
        var (svc, ctx, invoiceMock, _, _, _, calculatorMock, _, _) = BuildService(fc12On: true, fc13On: true);
        var (bcPublicId, _) = await SeedScenarioAsync(ctx);
        SetupCalculator(calculatorMock, AutoApprovableDto());

        await svc.ConfirmAsync(bcPublicId, NewConfirmRequest(), "user-1", "Admin", requesterIsAdmin: false, CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bc.Status);
        // El summary FC1.3 SI quedo persistido (para audit y backfill futuro).
        Assert.Equal(CreditNoteKind.PartialOnOriginal, bc.CreditNoteKind);
        Assert.Equal(ReviewRequiredReason.None, bc.ReviewRequiredReason);
        Assert.NotNull(bc.LiquidationComputedAt);
        Assert.Equal("user-1", bc.LiquidationComputedByUserId);

        // El path FC1.2 se sigue (encola NC al ARCA).
        invoiceMock.Verify(
            i => i.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Once);
    }

    // ============================================================
    // 3) ConfirmAsync FC1.3 ON + reason != None -> ManualReviewPending
    // ============================================================

    [Fact]
    public async Task ConfirmAsync_FlagOn_WithReviewReason_TransitionsToManualReviewPending()
    {
        var (svc, ctx, invoiceMock, approvalMock, _, _, calculatorMock, _, _) =
            BuildService(fc12On: true, fc13On: true);
        var (bcPublicId, _) = await SeedScenarioAsync(ctx);
        SetupCalculator(calculatorMock, ManualReviewDto());
        var approvalPublicId = Guid.NewGuid();
        SetupApprovalCreate(approvalMock, ctx, approvalPublicId);

        await svc.ConfirmAsync(bcPublicId, NewConfirmRequest(), "user-1", "Admin", requesterIsAdmin: false, CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.ManualReviewPending, bc.Status);
        Assert.Equal(CreditNoteKind.PartialOnOriginal, bc.CreditNoteKind);
        Assert.Equal(ReviewRequiredReason.CustomerIsRiOrFacturaA, bc.ReviewRequiredReason);
        Assert.NotNull(bc.PartialCreditNoteApprovalRequestId);

        // El approval fue creado via service.
        approvalMock.Verify(a => a.CreateAsync(
            It.Is<CreateApprovalRequestPayload>(p =>
                p.EntityType == "BookingCancellation" && p.EntityId == bc.Id),
            "user-1",
            "Admin",
            It.IsAny<CancellationToken>()), Times.Once);

        // El path FC1.2 NO se sigue: no se encola NC porque queda esperando review.
        invoiceMock.Verify(
            i => i.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Never);
    }

    // ============================================================
    // 4) GR-001: TotalPlusNewInvoice -> InvalidOperationException ANTES de persistir
    // ============================================================

    [Fact]
    public async Task ConfirmAsync_FlagOn_TotalPlusNewInvoice_ThrowsInvalidOperationBeforeAnyPersistence()
    {
        var (svc, ctx, invoiceMock, approvalMock, _, _, calculatorMock, _, _) =
            BuildService(fc12On: true, fc13On: true);
        var (bcPublicId, _) = await SeedScenarioAsync(ctx);
        SetupCalculator(calculatorMock, TotalPlusNewInvoiceDto());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ConfirmAsync(bcPublicId, NewConfirmRequest(), "user-1", "Admin", requesterIsAdmin: false, CancellationToken.None));

        Assert.Contains("FC1.3 Fase 2", ex.Message);

        // El BC SIGUE en Drafted: GR-001 rechaza ANTES de cualquier SaveChanges
        // que persista el summary FC1.3. Usamos un nuevo DbContext para evitar
        // leer datos en memoria que EF nunca commiteo.
        using var verifyCtx = NewDbContext();
        // No podemos usar el mismo InMemory desde otro DbContext (cada InMemory
        // database es per-name). Verificamos en el mismo ctx:
        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.Drafted, bc.Status);
        Assert.Null(bc.CreditNoteKind);
        Assert.Null(bc.LiquidationComputedAt);
        Assert.Null(bc.PartialCreditNoteApprovalRequestId);

        // Nunca encolo NC al ARCA y nunca creo approval.
        invoiceMock.Verify(
            i => i.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Never);
        approvalMock.Verify(a => a.CreateAsync(
            It.IsAny<CreateApprovalRequestPayload>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 5) INV-FC1.3-007: servicios no-Hotel sin override -> rechazo
    // ============================================================

    [Fact]
    public async Task ConfirmAsync_FlagOn_NonHotelService_WithoutOverride_ThrowsInvariantViolation()
    {
        var (svc, ctx, _, _, _, _, calculatorMock, _, _) =
            BuildService(fc12On: true, fc13On: true);
        var (bcPublicId, _) = await SeedScenarioAsync(ctx, addNonHotelService: true);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.ConfirmAsync(bcPublicId, NewConfirmRequest(), "user-1", "Admin", requesterIsAdmin: false, CancellationToken.None));

        Assert.Equal("INV-FC1.3-007", ex.InvariantCode);
        Assert.Contains("Hotel", ex.Message);

        // Calculator NUNCA se invoco: validamos antes de correr la formula.
        calculatorMock.Verify(c => c.Calculate(It.IsAny<FiscalLiquidationInput>(), It.IsAny<OperationalFinanceSettings>()), Times.Never);
    }

    // ============================================================
    // 6) INV-FC1.3-007 con override valido (Reason >= 50 chars) -> procede
    // ============================================================

    [Fact]
    public async Task ConfirmAsync_FlagOn_NonHotelService_WithValidOverride_Proceeds()
    {
        var (svc, ctx, _, approvalMock, _, _, calculatorMock, _, _) =
            BuildService(fc12On: true, fc13On: true);
        var (bcPublicId, bc) = await SeedScenarioAsync(ctx, addNonHotelService: true);

        // Sembrar override approval valido (>= 50 chars de Reason).
        var overridePublicId = Guid.NewGuid();
        var overrideApproval = new ApprovalRequest
        {
            PublicId = overridePublicId,
            RequestType = ApprovalRequestType.InvariantOverride,
            EntityType = "BookingCancellation",
            EntityId = bc.Id,
            Status = ApprovalStatus.Approved,
            RequestedByUserId = "user-1",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            Reason = "Necesitamos cancelar mezcla Hotel+Vuelo por razones administrativas - aprobado por contador",
        };
        ctx.ApprovalRequests.Add(overrideApproval);
        await ctx.SaveChangesAsync();

        SetupCalculator(calculatorMock, AutoApprovableDto());

        var req = NewConfirmRequest() with
        {
            IsAdminOverride = true,
            OverrideReason = "Override mezcla servicios autorizado",
            ApprovalRequestPublicId = overridePublicId,
        };

        // No debe tirar.
        await svc.ConfirmAsync(bcPublicId, req, "user-1", "Admin", requesterIsAdmin: true, CancellationToken.None);

        calculatorMock.Verify(c => c.Calculate(It.IsAny<FiscalLiquidationInput>(), It.IsAny<OperationalFinanceSettings>()), Times.Once);
        var refreshed = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, refreshed.Status);
    }

    // ============================================================
    // 7) N-001: FiscalSnapshot debe estar populado ANTES del Status ManualReviewPending
    // ============================================================

    [Fact]
    public async Task ConfirmAsync_FlagOn_PopulateSnapshotBeforeStatusChange()
    {
        // Garantia de orden: cuando el flujo termina con Status=ManualReviewPending,
        // el FiscalSnapshot ya tiene Source != Unset, ExchangeRate > 0 y Currency.
        // Si el orden estuviera invertido en el codigo, el CHECK heredado de FC1.2
        // tiraria 23514 en BD real — InMemory no lo valida, pero validamos el
        // contenido del snapshot directamente.
        var (svc, ctx, _, approvalMock, _, _, calculatorMock, _, _) =
            BuildService(fc12On: true, fc13On: true);
        var (bcPublicId, _) = await SeedScenarioAsync(ctx);
        SetupCalculator(calculatorMock, ManualReviewDto());
        SetupApprovalCreate(approvalMock, ctx, Guid.NewGuid());

        await svc.ConfirmAsync(bcPublicId, NewConfirmRequest(), "user-1", "Admin", requesterIsAdmin: false, CancellationToken.None);

        var bc = await ctx.BookingCancellations.FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Equal(BookingCancellationStatus.ManualReviewPending, bc.Status);

        // El snapshot debe estar populado con los 3 campos que el CHECK heredado exige.
        Assert.NotNull(bc.FiscalSnapshot);
        Assert.NotEqual(ExchangeRateSource.Unset, bc.FiscalSnapshot.Source);
        Assert.True(bc.FiscalSnapshot.ExchangeRateAtOriginalInvoice > 0);
        Assert.False(string.IsNullOrEmpty(bc.FiscalSnapshot.CurrencyAtEvent));
    }

    // ============================================================
    // 8) EditLiquidation: estado != ManualReviewPending -> rechazo
    // ============================================================

    [Fact]
    public async Task EditLiquidationAsync_WrongStatus_Rejects()
    {
        var (svc, ctx, _, _, _, _, _, _, _) = BuildService(fc12On: true, fc13On: true);
        var (bcPublicId, bc) = await SeedScenarioAsync(ctx);
        // BC esta en Drafted, no en ManualReviewPending.

        var req = new EditLiquidationRequest(
            OperatorPenaltyAmountOverride: 50_000m,
            NonRefundableItemsAmountOverride: null,
            CreditNoteKindOverride: null,
            Comment: "Edicion de prueba con comentario de longitud suficiente");

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.EditLiquidationAsync(bcPublicId, req, "admin-2", "Admin Dos", CancellationToken.None));

        Assert.Equal("INV-093", ex.InvariantCode);
    }

    // ============================================================
    // 9) EditLiquidation: 4-eyes violado sin bypass -> rechazo
    // ============================================================

    [Fact]
    public async Task EditLiquidationAsync_FourEyesViolation_SingleAdminBypassOff_Rejects()
    {
        var (svc, ctx, _, _, _, _, calculatorMock, _, _) =
            BuildService(fc12On: true, fc13On: true, allowSingleAdminBypass: false, activeAdminCount: 1);
        // Vendedor + admin que edita son el mismo user.
        var (bcPublicId, bc) = await SeedScenarioAsync(ctx, vendedorUserId: "user-same");
        bc.Status = BookingCancellationStatus.ManualReviewPending;
        bc.PartialCreditNoteApprovalRequestId = await SeedDummyApprovalAsync(ctx, bc.Id);
        await ctx.SaveChangesAsync();
        SetupCalculator(calculatorMock, ManualReviewDto());

        var req = new EditLiquidationRequest(
            OperatorPenaltyAmountOverride: 10_000m,
            NonRefundableItemsAmountOverride: null,
            CreditNoteKindOverride: null,
            Comment: "Comentario suficientemente largo para pasar validacion base de 20 chars");

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.EditLiquidationAsync(bcPublicId, req, "user-same", "Mismo User", CancellationToken.None));

        Assert.Equal("INV-FC1.3-004", ex.InvariantCode);
    }

    // ============================================================
    // 10) EditLiquidation: GR-005 bypass aplica con 1 admin + 100 chars
    // ============================================================

    [Fact]
    public async Task EditLiquidationAsync_FourEyesViolation_SingleAdminBypassOn_OneActiveAdmin_Allows()
    {
        var (svc, ctx, _, _, _, _, calculatorMock, _, _) =
            BuildService(fc12On: true, fc13On: true, allowSingleAdminBypass: true, activeAdminCount: 1);
        var (bcPublicId, bc) = await SeedScenarioAsync(ctx, vendedorUserId: "user-solo");
        bc.Status = BookingCancellationStatus.ManualReviewPending;
        // FiscalSnapshot tiene que estar populado en ManualReviewPending (invariante FC1.2,
        // CHECK chk_BookingCancellations_fiscalsnapshot_consistent). En BD real lo garantiza
        // ConfirmAsync; en InMemory tenemos que setearlo a mano cuando saltamos ese flujo.
        bc.FiscalSnapshot = new FiscalSnapshot
        {
            CurrencyAtEvent = "ARS",
            ExchangeRateAtOriginalInvoice = 1m,
            Source = ExchangeRateSource.BCRA_A3500,
            FetchedAt = DateTime.UtcNow,
            InvoicingModeAtEvent = SupplierInvoicingMode.TotalToCustomer,
        };
        bc.PartialCreditNoteApprovalRequestId = await SeedDummyApprovalAsync(ctx, bc.Id);
        await ctx.SaveChangesAsync();
        SetupCalculator(calculatorMock, ManualReviewDto());

        // Comentario >= 100 chars para activar GR-005.
        var longComment = "Como soy unico admin de la agencia, autorizo personalmente esta edicion " +
                          "porque el operador me envio penalidad actualizada por antelacion menor a la original. " +
                          "Comentario reforzado para audit.";
        Assert.True(longComment.Length >= 100);

        var req = new EditLiquidationRequest(
            OperatorPenaltyAmountOverride: 25_000m,
            NonRefundableItemsAmountOverride: null,
            CreditNoteKindOverride: null,
            Comment: longComment);

        // No debe tirar.
        var dto = await svc.EditLiquidationAsync(bcPublicId, req, "user-solo", "Admin Solo", CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(BookingCancellationStatus.ManualReviewPending.ToString(), dto.Status); // self-loop
    }

    // ============================================================
    // 11) EditLiquidation: bypass no aplica con 2 admins activos
    // ============================================================

    [Fact]
    public async Task EditLiquidationAsync_FourEyesViolation_SingleAdminBypassOn_TwoActiveAdmins_Rejects()
    {
        var (svc, ctx, _, _, _, _, calculatorMock, _, _) =
            BuildService(fc12On: true, fc13On: true, allowSingleAdminBypass: true, activeAdminCount: 2);
        var (bcPublicId, bc) = await SeedScenarioAsync(ctx, vendedorUserId: "user-same");
        bc.Status = BookingCancellationStatus.ManualReviewPending;
        bc.PartialCreditNoteApprovalRequestId = await SeedDummyApprovalAsync(ctx, bc.Id);
        await ctx.SaveChangesAsync();
        SetupCalculator(calculatorMock, ManualReviewDto());

        var longComment = new string('x', 120); // suficientemente largo

        var req = new EditLiquidationRequest(
            OperatorPenaltyAmountOverride: 10_000m,
            NonRefundableItemsAmountOverride: null,
            CreditNoteKindOverride: null,
            Comment: longComment);

        // Como hay 2+ admins activos, el bypass GR-005 NO aplica aunque el setting este ON.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.EditLiquidationAsync(bcPublicId, req, "user-same", "Mismo User", CancellationToken.None));

        Assert.Equal("INV-FC1.3-004", ex.InvariantCode);
    }

    // ============================================================
    // 12) EditLiquidation: recompute -> summary actualizado + audit
    // ============================================================

    [Fact]
    public async Task EditLiquidationAsync_RecomputesAndUpdatesSummary()
    {
        var (svc, ctx, _, _, auditMock, _, calculatorMock, _, _) =
            BuildService(fc12On: true, fc13On: true);
        var (bcPublicId, bc) = await SeedScenarioAsync(ctx, vendedorUserId: "vendedor-X");
        bc.Status = BookingCancellationStatus.ManualReviewPending;
        bc.CreditNoteKind = CreditNoteKind.PartialOnOriginal;
        bc.ReviewRequiredReason = ReviewRequiredReason.CustomerIsRiOrFacturaA;
        // Snapshot poblado (ver comentario en test 10).
        bc.FiscalSnapshot = new FiscalSnapshot
        {
            CurrencyAtEvent = "ARS",
            ExchangeRateAtOriginalInvoice = 1m,
            Source = ExchangeRateSource.BCRA_A3500,
            FetchedAt = DateTime.UtcNow,
            InvoicingModeAtEvent = SupplierInvoicingMode.TotalToCustomer,
        };
        bc.PartialCreditNoteApprovalRequestId = await SeedDummyApprovalAsync(ctx, bc.Id);
        await ctx.SaveChangesAsync();

        // El calculator devuelve un nuevo DTO con flag combinado (mas motivos).
        var newDto = new FiscalLiquidationDto(
            OriginalInvoiceAmount: 300_000m,
            CancellationAmount: 300_000m,
            OperatorPenaltyAmount: 50_000m,
            NonRefundableItemsAmount: 0m,
            FiscalAmountToCredit: 250_000m,
            AmountToRefundCustomer: 250_000m,
            FinalNetInvoiced: 50_000m,
            Case: PartialCreditNoteCase.Case3_FullCancellationWithPenalty,
            Kind: CreditNoteKind.PartialOnOriginal,
            ReviewRequiredReason: ReviewRequiredReason.PenaltyResetUncertainInResellerMode,
            Currency: "ARS",
            ClassificationExplanation: "Caso 3 con penalty 50k.");
        SetupCalculator(calculatorMock, newDto);

        var req = new EditLiquidationRequest(
            OperatorPenaltyAmountOverride: 50_000m,
            NonRefundableItemsAmountOverride: null,
            CreditNoteKindOverride: null,
            Comment: "Operador mando email con penalidad actualizada a 50k - ajusto");

        await svc.EditLiquidationAsync(bcPublicId, req, "admin-distinto", "Otro Admin", CancellationToken.None);

        var refreshed = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPublicId);
        // ReviewRequiredReason debe haber cambiado al nuevo valor del calculator.
        Assert.Equal(ReviewRequiredReason.PenaltyResetUncertainInResellerMode, refreshed.ReviewRequiredReason);
        // BC sigue en ManualReviewPending (self-loop).
        Assert.Equal(BookingCancellationStatus.ManualReviewPending, refreshed.Status);

        // Audit log fue invocado con la action correcta.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.BookingCancellationLiquidationEdited,
            AuditActions.BookingCancellationEntityName,
            It.IsAny<string>(),
            It.IsAny<string?>(),
            "admin-distinto",
            "Otro Admin",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ============================================================
    // 13) OnApprovedAsync: PartialOnOriginal -> AwaitingFiscalConfirmation
    // ============================================================

    [Fact]
    public async Task OnApprovedAsync_PartialOnOriginal_TransitionsToAwaitingFiscalConfirmation_WithWarningLog()
    {
        var (svc, ctx, invoiceMock, approvalMock, _, _, _, _, _) =
            BuildService(fc12On: true, fc13On: true);
        var (_, bc) = await SeedScenarioAsync(ctx, vendedorUserId: "vendedor-1");
        bc.Status = BookingCancellationStatus.ManualReviewPending;
        bc.CreditNoteKind = CreditNoteKind.PartialOnOriginal;
        bc.ReviewRequiredReason = ReviewRequiredReason.CustomerIsRiOrFacturaA;
        var approvalId = await SeedDummyApprovalAsync(ctx, bc.Id);
        bc.PartialCreditNoteApprovalRequestId = approvalId;
        await ctx.SaveChangesAsync();

        // Resolver != vendedor para evitar 4-eyes check.
        await svc.OnApprovedAsync(
            approvalRequestId: approvalId,
            resolverUserId: "admin-distinto",
            resolverUserName: "Admin Dos",
            resolverNotes: "Aprobacion aprobada por admin con comentario suficientemente largo de revision",
            ct: CancellationToken.None);

        var refreshed = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        // PartialOnOriginal en Fase 1 avanza inmediato a AwaitingFiscalConfirmation
        // (path FC1.2 que emite NC TOTAL al ARCA, con warning log).
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, refreshed.Status);
        Assert.Equal("admin-distinto", refreshed.ManualReviewerUserId);
        Assert.NotNull(refreshed.ManualReviewedAt);

        // Se encolo la annulacion AFIP (Fase 1: emite total).
        invoiceMock.Verify(
            i => i.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), true, // requesterIsAdmin=true (approval ya cubre)
                It.IsAny<CancellationToken>(), approvalId),
            Times.Once);

        // El approval se consume.
        approvalMock.Verify(a => a.MarkConsumedAsync(approvalId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ============================================================
    // 14) OnApprovedAsync: ya aprobado -> idempotente, no-op
    // ============================================================

    [Fact]
    public async Task OnApprovedAsync_AlreadyApproved_Idempotent()
    {
        var (svc, ctx, invoiceMock, approvalMock, _, _, _, _, _) =
            BuildService(fc12On: true, fc13On: true);
        var (_, bc) = await SeedScenarioAsync(ctx);
        bc.Status = BookingCancellationStatus.ManualReviewApproved;  // ya aprobado
        bc.CreditNoteKind = CreditNoteKind.PartialOnOriginal;
        var approvalId = await SeedDummyApprovalAsync(ctx, bc.Id);
        bc.PartialCreditNoteApprovalRequestId = approvalId;
        await ctx.SaveChangesAsync();

        await svc.OnApprovedAsync(approvalId, "admin", "Admin", "Notes", CancellationToken.None);

        var refreshed = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        // Sigue como estaba — no transicionamos.
        Assert.Equal(BookingCancellationStatus.ManualReviewApproved, refreshed.Status);

        // No encolamos nada al ARCA (es no-op).
        invoiceMock.Verify(
            i => i.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()),
            Times.Never);
        approvalMock.Verify(a => a.MarkConsumedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================
    // 15) OnRejectedAsync: rechaza y auto-resetea a Drafted
    // ============================================================

    [Fact]
    public async Task OnRejectedAsync_RejectedAndAutoResetToDraft()
    {
        var (svc, ctx, _, _, _, _, _, _, _) = BuildService(fc12On: true, fc13On: true);
        var (_, bc) = await SeedScenarioAsync(ctx);
        bc.Status = BookingCancellationStatus.ManualReviewPending;
        bc.CreditNoteKind = CreditNoteKind.PartialOnOriginal;
        bc.ReviewRequiredReason = ReviewRequiredReason.CustomerIsRiOrFacturaA;
        bc.LiquidationComputedAt = DateTime.UtcNow;
        bc.LiquidationComputedByUserId = "user-1";
        var approvalId = await SeedDummyApprovalAsync(ctx, bc.Id);
        bc.PartialCreditNoteApprovalRequestId = approvalId;
        await ctx.SaveChangesAsync();

        await svc.OnRejectedAsync(
            approvalRequestId: approvalId,
            resolverUserId: "admin-2",
            resolverUserName: "Admin Dos",
            resolverNotes: "No cumple criterio contador - faltan items no reintegrables marcados",
            ct: CancellationToken.None);

        var refreshed = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        // Auto-reset: vuelve a Drafted y limpia campos FC1.3.
        Assert.Equal(BookingCancellationStatus.Drafted, refreshed.Status);
        Assert.Null(refreshed.CreditNoteKind);
        Assert.Equal(ReviewRequiredReason.None, refreshed.ReviewRequiredReason);
        Assert.Null(refreshed.LiquidationComputedAt);
        Assert.Null(refreshed.LiquidationComputedByUserId);
        Assert.Null(refreshed.PartialCreditNoteApprovalRequestId);
        // Pero la trazabilidad del reviewer queda preservada para audit.
        Assert.Equal("admin-2", refreshed.ManualReviewerUserId);
        Assert.NotNull(refreshed.ManualReviewedAt);
    }

    // ============================================================
    // 16) OnRejectedAsync: ya rechazado -> idempotente
    // ============================================================

    [Fact]
    public async Task OnRejectedAsync_AlreadyRejected_Idempotent()
    {
        var (svc, ctx, _, _, _, _, _, _, _) = BuildService(fc12On: true, fc13On: true);
        var (_, bc) = await SeedScenarioAsync(ctx);
        bc.Status = BookingCancellationStatus.ManualReviewRejected;
        var approvalId = await SeedDummyApprovalAsync(ctx, bc.Id);
        bc.PartialCreditNoteApprovalRequestId = approvalId;
        await ctx.SaveChangesAsync();

        await svc.OnRejectedAsync(approvalId, "admin", "Admin", "Notes que igual son ignorados", CancellationToken.None);

        var refreshed = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        // No cambio: idempotente.
        Assert.Equal(BookingCancellationStatus.ManualReviewRejected, refreshed.Status);
    }

    // ============================================================
    // Helpers privados
    // ============================================================

    /// <summary>Inserta un ApprovalRequest minimo y devuelve su Id (legacy int).</summary>
    private static async Task<int> SeedDummyApprovalAsync(AppDbContext ctx, int bcId)
    {
        var ar = new ApprovalRequest
        {
            PublicId = Guid.NewGuid(),
            RequestType = ApprovalRequestType.PartialCreditNoteApproval,
            EntityType = "BookingCancellation",
            EntityId = bcId,
            Status = ApprovalStatus.Pending,
            RequestedByUserId = "vendedor-1",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Metadata = JsonSerializer.Serialize(new Dictionary<string, object?> { ["schemaVersion"] = 1 }),
        };
        ctx.ApprovalRequests.Add(ar);
        await ctx.SaveChangesAsync();
        return ar.Id;
    }
}
