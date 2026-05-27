using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.3 Fase 2 integration tests (plan tactico Fase 2 §FC1.3.F2.1, 2026-05-26):
/// valida la persistencia del owned VO <c>FiscalLiquidation</c> contra Postgres real:
///  - El backfill SQL de la migracion Fase2_M1 (pre-check defensivo RH-001 + UPDATE
///    idempotente + skip de rechazados).
///  - El doble-write invariante RH-002 (Confirm + EditLiquidation escriben Metadata
///    JSON Y columnas FiscalLiquidation_* coherentes).
///  - El CHECK constraint de suma rechazado por Postgres (SqlState 23514).
///
/// <para>
/// <b>Por que Postgres real (no InMemory)</b>: estos tests validan SQL crudo
/// (RAISE EXCEPTION del backfill, CHECK constraint, jsonb). InMemory no ejecuta
/// nada de eso. Solo COMPILAN en la maquina dev (sin Postgres local) — los corre
/// el VPS con Docker.
/// </para>
///
/// <para>
/// <b>Sobre el backfill</b>: la fixture usa <c>EnsureCreatedAsync</c> (schema desde
/// el modelo), NO <c>MigrateAsync</c>. Por eso los tests de backfill ejecutan el
/// SQL del backfill (pasos 5.A/5.B/5.C de la migracion) directamente via
/// <c>ExecuteSqlRawAsync</c>. El SQL esta replicado en las constantes de abajo;
/// si cambia en la migracion Fase2_M1, hay que actualizarlo aca (mismo patron que
/// los CHECK constraints en <see cref="PostgresIntegrationFixture"/>).
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class FiscalLiquidationBackfillIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public FiscalLiquidationBackfillIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // SQL del backfill — replicado de la migracion Fase2_M1 (mantener sincronizado).
    // =========================================================================

    // Paso 5.A — pre-check defensivo (aborta con RAISE EXCEPTION si hay Metadata invalido).
    private const string BackfillPreCheckSql = """
        DO $$
        DECLARE
          v_problematic_count int;
        BEGIN
          SELECT COUNT(*) INTO v_problematic_count
          FROM "ApprovalRequests" ar
          WHERE ar."RequestType" = 11
            AND (
              ar."Metadata" IS NULL
              OR length(trim(ar."Metadata")) = 0
              OR jsonb_typeof(ar."Metadata"::jsonb) IS DISTINCT FROM 'object'
              OR NOT (ar."Metadata"::jsonb ? 'originalInvoiceAmount')
              OR NOT (ar."Metadata"::jsonb ? 'fiscalAmountToCredit')
              OR NOT (ar."Metadata"::jsonb ? 'currency')
            );

          IF v_problematic_count > 0 THEN
            RAISE EXCEPTION 'FC1.3.F2.1 backfill ABORTED: % ApprovalRequests tipo 11 con Metadata invalido o claves criticas faltantes. Correr tools/sql/fase2-m1-prevalidation-metadata.sql para identificar filas y limpiarlas ANTES de re-aplicar la migracion.', v_problematic_count;
          END IF;

          RAISE NOTICE 'FC1.3.F2.1 paso 5.A OK: 0 filas problematicas en ApprovalRequests tipo 11.';
        END $$;
        """;

    // Paso 5.B — UPDATE idempotente acotado a filas seguras.
    private const string BackfillUpdateSql = """
        UPDATE "BookingCancellations" bc
        SET
          "FiscalLiquidation_OriginalInvoiceAmount" = (m.meta->>'originalInvoiceAmount')::numeric,
          "FiscalLiquidation_CancellationAmount"    = (m.meta->>'cancellationAmount')::numeric,
          "FiscalLiquidation_OperatorPenaltyAmount" = (m.meta->>'operatorPenaltyAmount')::numeric,
          "FiscalLiquidation_NonRefundableItemsAmount" = (m.meta->>'nonRefundableItemsAmount')::numeric,
          "FiscalLiquidation_FiscalAmountToCredit"  = (m.meta->>'fiscalAmountToCredit')::numeric,
          "FiscalLiquidation_AmountToRefundCustomer"= (m.meta->>'amountToRefundCustomer')::numeric,
          "FiscalLiquidation_FinalNetInvoiced"      = (m.meta->>'finalNetInvoiced')::numeric,
          -- I3 fix: NULLIF(trim(...), '') colapsa currency vacio a NULL para que el
          -- COALESCE caiga al default 'ARS' (mismo SQL que la migracion Fase2_M1).
          "FiscalLiquidation_Currency"              = COALESCE(NULLIF(trim(m.meta->>'currency'), ''), 'ARS'),
          "FiscalLiquidation_ComputedAt"            = bc."LiquidationComputedAt",
          "FiscalLiquidation_ComputedByUserId"      = m.meta->>'computedByUserId',
          "FiscalLiquidation_ComputedByUserName"    = m.meta->>'computedByUserName'
        FROM (
          SELECT ar."Id" as id, ar."Metadata"::jsonb as meta
          FROM "ApprovalRequests" ar
          WHERE ar."RequestType" = 11
            AND ar."Metadata" IS NOT NULL
            AND jsonb_typeof(ar."Metadata"::jsonb) = 'object'
            AND ar."Metadata"::jsonb ? 'originalInvoiceAmount'
            AND ar."Metadata"::jsonb ? 'fiscalAmountToCredit'
            AND ar."Metadata"::jsonb ? 'currency'
            -- B-FISC-1: excluir CommissionOnly (mismo SQL que la migracion Fase2_M1).
            AND (ar."Metadata"::jsonb->>'computedCase') IS DISTINCT FROM 'Case5_CommissionOnlyPartial'
            AND (ar."Metadata"::jsonb->>'computedCase') IS DISTINCT FROM 'Case6_CommissionOnlyFull'
        ) m
        WHERE bc."PartialCreditNoteApprovalRequestId" = m.id
          AND bc."FiscalLiquidation_FiscalAmountToCredit" IS NULL;
        """;

    /// <summary>Corre el backfill completo (pre-check + update) como lo haria la migracion.</summary>
    private static async Task RunBackfillAsync(AppDbContext ctx)
    {
        await ctx.Database.ExecuteSqlRawAsync(BackfillPreCheckSql);
        await ctx.Database.ExecuteSqlRawAsync(BackfillUpdateSql);
    }

    // =========================================================================
    // Helpers de seed.
    // =========================================================================

    /// <summary>
    /// Crea un Customer + Supplier + Reserva minimos para colgar un BC.
    /// Devuelve los ids necesarios.
    /// </summary>
    private static async Task<(int CustomerId, int SupplierId, int ReservaId)> SeedBaseAsync(
        AppDbContext ctx,
        SupplierInvoicingMode supplierMode = SupplierInvoicingMode.TotalToCustomer)
    {
        var customer = new Customer { FullName = "Cliente Backfill", TaxCondition = "Consumidor Final", IsActive = true };
        // supplierMode: TotalToCustomer (reseller, default) o CommissionOnly
        // (intermediario). El calculator usa Supplier.InvoicingMode como modo del
        // operador cuando el snapshot no trae InvoicingModeAtEvent (caso de
        // ConfirmAsync, que no lo setea). En CommissionOnly el calculator hace
        // early-exit (GR-003) y NO persiste el VO (B-FISC-1).
        var supplier = new Supplier
        {
            Name = "Operador Backfill",
            TaxCondition = "IVA_RESP_INSCRIPTO",
            IsActive = true,
            InvoicingMode = supplierMode,
        };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"BF-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Reserva Backfill",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        return (customer.Id, supplier.Id, reserva.Id);
    }

    /// <summary>
    /// Crea una factura origen minima para el BC (el FK OriginatingInvoiceId es required).
    /// </summary>
    private static async Task<int> SeedInvoiceAsync(AppDbContext ctx, int reservaId, decimal importeTotal, int tipoComprobante = 6)
    {
        var importeNeto = Math.Round(importeTotal / 1.21m, 2);
        var invoice = new Invoice
        {
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            CAE = "12345678901234",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            Resultado = "A",
            ImporteTotal = importeTotal,
            ImporteNeto = importeNeto,
            ImporteIva = importeTotal - importeNeto,
            ReservaId = reservaId,
            AnnulmentStatus = AnnulmentStatus.None,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        return invoice.Id;
    }

    /// <summary>
    /// Serializa un Metadata JSON valido (schemaVersion=1) con todas las claves que
    /// el backfill lee. Permite simular lo que Fase 1 (SubmitForReviewAsync) escribia.
    /// </summary>
    private static string BuildMetadataJson(
        decimal originalInvoiceAmount,
        decimal fiscalAmountToCredit,
        decimal operatorPenaltyAmount,
        decimal nonRefundableItemsAmount,
        decimal amountToRefundCustomer,
        decimal finalNetInvoiced,
        string currency = "ARS",
        DateTime? computedAt = null,
        string computedByUserId = "vendedor-1",
        string computedByUserName = "Juan Vendedor",
        string? computedCase = null)
    {
        var dict = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["computedAt"] = computedAt ?? DateTime.UtcNow,
            ["computedByUserId"] = computedByUserId,
            ["computedByUserName"] = computedByUserName,
            ["originalInvoiceAmount"] = originalInvoiceAmount,
            ["cancellationAmount"] = originalInvoiceAmount,
            ["operatorPenaltyAmount"] = operatorPenaltyAmount,
            ["nonRefundableItemsAmount"] = nonRefundableItemsAmount,
            ["fiscalAmountToCredit"] = fiscalAmountToCredit,
            ["amountToRefundCustomer"] = amountToRefundCustomer,
            ["finalNetInvoiced"] = finalNetInvoiced,
            ["currency"] = currency,
        };
        // computedCase solo se agrega si el caller lo pasa. El backfill lo usa para
        // excluir CommissionOnly (B-FISC-1). Replica el ["computedCase"] que escribe
        // SubmitForReviewAsync (= liquidation.Case.ToString()).
        if (computedCase is not null)
        {
            dict["computedCase"] = computedCase;
        }
        return JsonSerializer.Serialize(dict);
    }

    /// <summary>
    /// Crea un ApprovalRequest tipo 11 (PartialCreditNoteApproval) con el Metadata
    /// indicado, y un BC en ManualReviewPending vinculado por FK + con
    /// LiquidationComputedAt seteado (precondicion del CHECK de consistencia).
    /// </summary>
    private static async Task<(int BcId, int ApprovalId, DateTime ComputedAt)> SeedBcWithPendingApprovalAsync(
        AppDbContext ctx,
        int customerId,
        int supplierId,
        int reservaId,
        int invoiceId,
        string metadataJson,
        BookingCancellationStatus status = BookingCancellationStatus.ManualReviewPending)
    {
        var approval = new ApprovalRequest
        {
            RequestType = ApprovalRequestType.PartialCreditNoteApproval,
            EntityType = "BookingCancellation",
            EntityId = 0, // se ajusta despues de crear el BC (no es critico para el backfill)
            RequestedByUserId = "vendedor-1",
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "NC parcial backfill test",
            Metadata = metadataJson,
        };
        ctx.ApprovalRequests.Add(approval);
        await ctx.SaveChangesAsync();

        // LiquidationComputedAt: el CHECK de consistencia exige que, si el VO
        // ComputedAt no es null (lo setea el backfill desde esta columna), coincida.
        var computedAt = DateTime.UtcNow;

        var bc = new BookingCancellation
        {
            ReservaId = reservaId,
            CustomerId = customerId,
            SupplierId = supplierId,
            OriginatingInvoiceId = invoiceId,
            Status = status,
            Reason = "Backfill test BC",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            // El summary FC1.3 ya existe en BCs Fase 1 que pasaron por el calculator.
            CreditNoteKind = CreditNoteKind.PartialOnOriginal,
            ReviewRequiredReason = ReviewRequiredReason.CustomerIsRiOrFacturaA,
            LiquidationComputedAt = computedAt,
            LiquidationComputedByUserId = "vendedor-1",
            PartialCreditNoteApprovalRequestId = approval.Id,
            // Snapshot fiscal completo: el CHECK heredado exige esto en Status != Drafted/Aborted.
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.BCRA_A3500,
                ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS",
                FetchedAt = DateTime.UtcNow,
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc.Id, approval.Id, computedAt);
    }

    // =========================================================================
    // Service builder para los tests del doble-write (Confirm / EditLiquidation).
    // Patron copiado de BookingCancellationServicePartialCreditNoteIntegrationTests.
    // =========================================================================

    private (BookingCancellationService Service, AppDbContext Ctx, Mock<IInvoiceService> InvoiceMock) BuildService(
        AppDbContext ctx,
        bool allowBypassSingleAdmin = false,
        int activeAdminCount = 2)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnablePartialCreditNotes = true,
                Allow4EyesBypassWhenSingleAdmin = allowBypassSingleAdmin,
                OnePerReservaInvoicePolicy = true,
                OperatorRefundTimeoutDays = 60,
                PartialNcAutoApprovalThreshold = 500_000m,
                PartialNcAdminReviewThreshold = 2_000_000m,
                PartialNcAccountingReviewThreshold = null,
            });

        var approvalService = new ApprovalRequestService(ctx, settingsMock.Object);
        var auditRepo = new Repository<AuditLog>(ctx);
        var auditService = new AuditService(auditRepo, NullLogger<AuditService>.Instance);
        var calculator = new FiscalLiquidationCalculator(NullLogger<FiscalLiquidationCalculator>.Instance);
        var adminCountMock = new Mock<IAdminUserCountService>();
        adminCountMock
            .Setup(a => a.CountActiveAdminsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeAdminCount);

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalService, auditService,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object, calculator, adminCountMock.Object);

        return (service, ctx, invoiceMock);
    }

    /// <summary>Seed Hotel completo (Reserva + servicio Hotel + Invoice + Item + BC Drafted).</summary>
    private static async Task<Guid> SeedHotelBcDraftedAsync(
        AppDbContext ctx, int tipoComprobante, decimal importeTotal, string vendedorUserId = "vendedor-1",
        SupplierInvoicingMode supplierMode = SupplierInvoicingMode.TotalToCustomer)
    {
        var (customerId, supplierId, reservaId) = await SeedBaseAsync(ctx, supplierMode);

        var hotelService = new ServicioReserva
        {
            ReservaId = reservaId,
            CustomerId = customerId,
            SupplierId = supplierId,
            ProductType = ServiceTypes.Hotel,
            ServiceType = "Hotel",
            Description = "Hotel test FC1.3 Fase 2",
            DepartureDate = DateTime.UtcNow.AddDays(15),
        };
        ctx.Set<ServicioReserva>().Add(hotelService);
        await ctx.SaveChangesAsync();

        var invoiceId = await SeedInvoiceAsync(ctx, reservaId, importeTotal, tipoComprobante);

        var importeNeto = Math.Round(importeTotal / 1.21m, 2);
        ctx.Set<InvoiceItem>().Add(new InvoiceItem
        {
            InvoiceId = invoiceId,
            Description = "Hotel 5 noches paquete familiar",
            Quantity = 1,
            UnitPrice = importeTotal,
            Total = importeTotal,
            AlicuotaIvaId = 5,
            ImporteIva = importeTotal - importeNeto,
            IsRefundable = true,
            ItemCategory = InvoiceItemCategory.Service,
            SourceServicioReservaId = hotelService.Id,
        });
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reservaId,
            CustomerId = customerId,
            SupplierId = supplierId,
            OriginatingInvoiceId = invoiceId,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Cliente decidio cancelar 5 dias antes",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = vendedorUserId,
            DraftedByUserName = "Juan Vendedor",
            AmountPaidAtCancellation = importeTotal,
            EstimatedRefundAmount = importeTotal,
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Unset,
                ExchangeRateAtOriginalInvoice = 0m,
                CurrencyAtEvent = null,
                FetchedAt = default,
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();
        return bc.PublicId;
    }

    private static ConfirmCancellationRequest BuildValidConfirm()
        => new(
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

    // =========================================================================
    // 1) Backfill_FromExistingApprovalMetadata_PopulatesAllColumns
    // =========================================================================

    /// <summary>
    /// Seed 3 BCs en ManualReviewPending con Metadata JSON correcto. Corre el backfill.
    /// Verifica que las 10 columnas FiscalLiquidation_* quedan pobladas exactamente
    /// igual que el JSON.
    /// </summary>
    [Fact]
    public async Task Backfill_FromExistingApprovalMetadata_PopulatesAllColumns()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (customerId, supplierId, reservaId) = await SeedBaseAsync(ctx);

        // Tres BCs con montos distintos. Suma valida: fiscal + noReembolsable + penalty = original.
        // Ejemplo 1: original 100k = fiscal 90k + penalty 10k + noReemb 0.
        var bc1Meta = BuildMetadataJson(100_000m, 90_000m, 10_000m, 0m, 90_000m, 10_000m);
        // Ejemplo 2: original 200k = fiscal 150k + penalty 30k + noReemb 20k.
        var bc2Meta = BuildMetadataJson(200_000m, 150_000m, 30_000m, 20_000m, 150_000m, 50_000m);
        // Ejemplo 3: original 50k = fiscal 50k + 0 + 0 (cancelacion limpia sin retenciones).
        var bc3Meta = BuildMetadataJson(50_000m, 50_000m, 0m, 0m, 50_000m, 0m);

        var inv1 = await SeedInvoiceAsync(ctx, reservaId, 100_000m);
        var inv2 = await SeedInvoiceAsync(ctx, reservaId, 200_000m);
        var inv3 = await SeedInvoiceAsync(ctx, reservaId, 50_000m);

        var (bc1, _, _) = await SeedBcWithPendingApprovalAsync(ctx, customerId, supplierId, reservaId, inv1, bc1Meta);
        var (bc2, _, _) = await SeedBcWithPendingApprovalAsync(ctx, customerId, supplierId, reservaId, inv2, bc2Meta);
        var (bc3, _, _) = await SeedBcWithPendingApprovalAsync(ctx, customerId, supplierId, reservaId, inv3, bc3Meta);

        // ACT — correr el backfill.
        await RunBackfillAsync(ctx);

        // ASSERT — las 10 columnas pobladas igual que el JSON.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bc1After = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc1);
        Assert.NotNull(bc1After.FiscalLiquidation);
        Assert.Equal(100_000m, bc1After.FiscalLiquidation!.OriginalInvoiceAmount);
        Assert.Equal(90_000m, bc1After.FiscalLiquidation.FiscalAmountToCredit);
        Assert.Equal(10_000m, bc1After.FiscalLiquidation.OperatorPenaltyAmount);
        Assert.Equal(0m, bc1After.FiscalLiquidation.NonRefundableItemsAmount);
        Assert.Equal(90_000m, bc1After.FiscalLiquidation.AmountToRefundCustomer);
        Assert.Equal(10_000m, bc1After.FiscalLiquidation.FinalNetInvoiced);
        Assert.Equal("ARS", bc1After.FiscalLiquidation.Currency);
        // ComputedAt del VO debe coincidir con la columna summary (RH3-003).
        Assert.Equal(bc1After.LiquidationComputedAt, bc1After.FiscalLiquidation.ComputedAt);
        Assert.Equal("vendedor-1", bc1After.FiscalLiquidation.ComputedByUserId);

        var bc2After = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc2);
        Assert.NotNull(bc2After.FiscalLiquidation);
        Assert.Equal(150_000m, bc2After.FiscalLiquidation!.FiscalAmountToCredit);
        Assert.Equal(20_000m, bc2After.FiscalLiquidation.NonRefundableItemsAmount);

        var bc3After = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc3);
        Assert.NotNull(bc3After.FiscalLiquidation);
        Assert.Equal(50_000m, bc3After.FiscalLiquidation!.FiscalAmountToCredit);
    }

    // =========================================================================
    // 2) Backfill_SkipsRejectedBCs
    // =========================================================================

    /// <summary>
    /// BC en ManualReviewRejected con FK al approval nulled (Fase 1 nulea la FK en
    /// OnRejectedAsync). El backfill no toca esa fila: sin FK, el JOIN del paso 5.B
    /// no matchea, asi que las columnas FiscalLiquidation_* siguen null.
    /// </summary>
    [Fact]
    public async Task Backfill_SkipsRejectedBCs()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (customerId, supplierId, reservaId) = await SeedBaseAsync(ctx);
        var inv = await SeedInvoiceAsync(ctx, reservaId, 100_000m);

        // El approval existe (con Metadata valido), pero el BC NO lo referencia
        // (FK null) — simula un BC rechazado que nulea la FK.
        var meta = BuildMetadataJson(100_000m, 90_000m, 10_000m, 0m, 90_000m, 10_000m);
        var approval = new ApprovalRequest
        {
            RequestType = ApprovalRequestType.PartialCreditNoteApproval,
            EntityType = "BookingCancellation",
            EntityId = 0,
            RequestedByUserId = "vendedor-1",
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Rejected,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "Rechazado",
            Metadata = meta,
        };
        ctx.ApprovalRequests.Add(approval);
        await ctx.SaveChangesAsync();

        // BC en Drafted (post-reject auto-reset) SIN FK al approval.
        var bc = new BookingCancellation
        {
            ReservaId = reservaId,
            CustomerId = customerId,
            SupplierId = supplierId,
            OriginatingInvoiceId = inv,
            Status = BookingCancellationStatus.Drafted,
            Reason = "BC rechazado, FK nulled",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            PartialCreditNoteApprovalRequestId = null, // <- nulled por el reject
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Unset,
                ExchangeRateAtOriginalInvoice = 0m,
                CurrencyAtEvent = null,
                FetchedAt = default,
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        // ACT
        await RunBackfillAsync(ctx);

        // ASSERT — el VO sigue null (no se backfilleo).
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Null(bcAfter.FiscalLiquidation);
    }

    // =========================================================================
    // 3) Backfill_MissingKey_RaisesAndAborts (RH-001)
    // =========================================================================

    /// <summary>
    /// Seed un ApprovalRequest tipo 11 con Metadata al que le falta fiscalAmountToCredit
    /// y currency. El pre-check (paso 5.A) debe abortar con RAISE EXCEPTION cuyo mensaje
    /// contiene la cuenta de filas problematicas.
    /// </summary>
    [Fact]
    public async Task Backfill_MissingKey_RaisesAndAborts()
    {
        await using var ctx = _fixture.CreateDbContext();

        // Metadata con SOLO originalInvoiceAmount (faltan fiscalAmountToCredit + currency).
        var approval = new ApprovalRequest
        {
            RequestType = ApprovalRequestType.PartialCreditNoteApproval,
            EntityType = "BookingCancellation",
            EntityId = 0,
            RequestedByUserId = "vendedor-1",
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "Metadata incompleto",
            Metadata = """{"originalInvoiceAmount": 100}""",
        };
        ctx.ApprovalRequests.Add(approval);
        await ctx.SaveChangesAsync();

        // ACT + ASSERT — el pre-check tira PostgresException (RAISE EXCEPTION).
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ctx.Database.ExecuteSqlRawAsync(BackfillPreCheckSql));

        Assert.Contains("FC1.3.F2.1 backfill ABORTED", ex.MessageText);
        // El mensaje incluye la cuenta de filas problematicas (al menos 1).
        Assert.Contains("1 ApprovalRequests tipo 11", ex.MessageText);
    }

    // =========================================================================
    // 4) Backfill_MalformedMetadata_RaisesAndAborts (RH-001)
    // =========================================================================

    /// <summary>
    /// Seed un ApprovalRequest tipo 11 con Metadata que es un string JSON suelto
    /// (no un objeto). El pre-check debe abortar (jsonb_typeof != 'object').
    /// </summary>
    [Fact]
    public async Task Backfill_MalformedMetadata_RaisesAndAborts()
    {
        await using var ctx = _fixture.CreateDbContext();

        var approval = new ApprovalRequest
        {
            RequestType = ApprovalRequestType.PartialCreditNoteApproval,
            EntityType = "BookingCancellation",
            EntityId = 0,
            RequestedByUserId = "vendedor-1",
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "Metadata no objeto",
            // Un string JSON valido pero que NO es un objeto top-level.
            Metadata = "\"not a json object\"",
        };
        ctx.ApprovalRequests.Add(approval);
        await ctx.SaveChangesAsync();

        // ACT + ASSERT
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            ctx.Database.ExecuteSqlRawAsync(BackfillPreCheckSql));

        Assert.Contains("FC1.3.F2.1 backfill ABORTED", ex.MessageText);
    }

    // =========================================================================
    // 5) Confirm_PostFase2_BothRepresentationsMatch (RH-002)
    // =========================================================================

    /// <summary>
    /// Post-F2.1: crear un BC nuevo via ConfirmAsync (caso 8 Factura A -> manual review).
    /// Leer el Metadata JSON del approval Y las columnas FiscalLiquidation_*. Verificar
    /// que coinciden field by field (montos). Si la divergencia es > 0.01 en algun
    /// monto, test rojo.
    /// </summary>
    [Fact]
    public async Task Confirm_PostFase2_BothRepresentationsMatch()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (service, _, _) = BuildService(ctx);
        var bcPublicId = await SeedHotelBcDraftedAsync(ctx, tipoComprobante: 1, importeTotal: 100_000m);

        // ACT — Confirm de Factura A -> ManualReviewPending (escribe doble representacion).
        await service.ConfirmAsync(bcPublicId, BuildValidConfirm(), "vendedor-1", "Juan", false, CancellationToken.None);

        // ASSERT — leer ambas representaciones.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bc = await verifyCtx.BookingCancellations.AsNoTracking()
            .Include(b => b.PartialCreditNoteApprovalRequest)
            .FirstAsync(b => b.PublicId == bcPublicId);

        Assert.NotNull(bc.FiscalLiquidation);
        Assert.NotNull(bc.PartialCreditNoteApprovalRequest);
        Assert.NotNull(bc.PartialCreditNoteApprovalRequest!.Metadata);

        using var metaDoc = JsonDocument.Parse(bc.PartialCreditNoteApprovalRequest.Metadata!);
        var root = metaDoc.RootElement;

        // Comparar field by field. Tolerancia 0.01 (mismo umbral que el CHECK).
        AssertMoneyMatch(root, "originalInvoiceAmount", bc.FiscalLiquidation!.OriginalInvoiceAmount);
        AssertMoneyMatch(root, "fiscalAmountToCredit", bc.FiscalLiquidation.FiscalAmountToCredit);
        AssertMoneyMatch(root, "operatorPenaltyAmount", bc.FiscalLiquidation.OperatorPenaltyAmount);
        AssertMoneyMatch(root, "nonRefundableItemsAmount", bc.FiscalLiquidation.NonRefundableItemsAmount);
        AssertMoneyMatch(root, "amountToRefundCustomer", bc.FiscalLiquidation.AmountToRefundCustomer);
        AssertMoneyMatch(root, "finalNetInvoiced", bc.FiscalLiquidation.FinalNetInvoiced);

        // Currency coincide.
        Assert.Equal(root.GetProperty("currency").GetString(), bc.FiscalLiquidation.Currency);

        // El VO ComputedAt coincide con la columna summary (CHECK de consistencia, RH3-003).
        Assert.Equal(bc.LiquidationComputedAt, bc.FiscalLiquidation.ComputedAt);
    }

    /// <summary>Asegura que el monto del JSON y el de la columna no difieren mas de 0.01.</summary>
    private static void AssertMoneyMatch(JsonElement root, string jsonKey, decimal columnValue)
    {
        var jsonValue = root.GetProperty(jsonKey).GetDecimal();
        Assert.True(Math.Abs(jsonValue - columnValue) <= 0.01m,
            $"Divergencia en {jsonKey}: JSON={jsonValue} vs columna={columnValue}");
    }

    // =========================================================================
    // 6) EditLiquidation_PostFase2_UpdatesBothRepresentations (RH-002)
    // =========================================================================

    /// <summary>
    /// Admin edita la liquidacion via EditLiquidationAsync. B1 fix (RH-002): tras el
    /// edit el JSON TOP-LEVEL del Metadata y las columnas FiscalLiquidation_* tienen que
    /// quedar coherentes field-by-field (antes el top-level quedaba con valores pre-edit
    /// y solo edits[] reflejaba el cambio). Usamos un caso reseller (Factura A,
    /// TotalToCustomer) donde el VO SI existe para poder comparar columnas vs JSON.
    /// </summary>
    [Fact]
    public async Task EditLiquidation_PostFase2_UpdatesBothRepresentations()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (service, _, _) = BuildService(ctx);
        // tipoComprobante 1 = Factura A => manual review por CustomerIsRiOrFacturaA.
        // Supplier TotalToCustomer (reseller, default): el VO se persiste, NO es CommissionOnly.
        var bcPublicId = await SeedHotelBcDraftedAsync(ctx, tipoComprobante: 1, importeTotal: 100_000m, vendedorUserId: "vendedor-1");

        // Confirm -> ManualReviewPending (Factura A).
        await service.ConfirmAsync(bcPublicId, BuildValidConfirm(), "vendedor-1", "Juan", false, CancellationToken.None);

        // ACT — admin DISTINTO edita la penalty del operador.
        var editReq = new EditLiquidationRequest(
            OperatorPenaltyAmountOverride: 10_000m,
            NonRefundableItemsAmountOverride: null,
            CreditNoteKindOverride: null,
            Comment: "Operador respondio con penalty actualizada de $10k segun antelacion del cliente");

        await service.EditLiquidationAsync(bcPublicId, editReq, "admin-otra-persona", "Admin", CancellationToken.None);

        // ASSERT — las columnas reflejan la nueva penalty.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bc = await verifyCtx.BookingCancellations.AsNoTracking()
            .Include(b => b.PartialCreditNoteApprovalRequest)
            .FirstAsync(b => b.PublicId == bcPublicId);

        Assert.NotNull(bc.FiscalLiquidation);
        Assert.Equal(10_000m, bc.FiscalLiquidation!.OperatorPenaltyAmount);

        // B1 fix: el JSON TOP-LEVEL (no solo edits[]) tiene que coincidir con las
        // columnas field-by-field. Antes del fix, operatorPenaltyAmount top-level
        // quedaba en 0 (valor pre-edit) mientras la columna decia 10_000 -> divergencia.
        Assert.NotNull(bc.PartialCreditNoteApprovalRequest!.Metadata);
        using var metaDoc = JsonDocument.Parse(bc.PartialCreditNoteApprovalRequest.Metadata!);
        var root = metaDoc.RootElement;

        AssertMoneyMatch(root, "originalInvoiceAmount", bc.FiscalLiquidation.OriginalInvoiceAmount);
        AssertMoneyMatch(root, "fiscalAmountToCredit", bc.FiscalLiquidation.FiscalAmountToCredit);
        AssertMoneyMatch(root, "operatorPenaltyAmount", bc.FiscalLiquidation.OperatorPenaltyAmount);
        AssertMoneyMatch(root, "nonRefundableItemsAmount", bc.FiscalLiquidation.NonRefundableItemsAmount);
        AssertMoneyMatch(root, "amountToRefundCustomer", bc.FiscalLiquidation.AmountToRefundCustomer);
        AssertMoneyMatch(root, "finalNetInvoiced", bc.FiscalLiquidation.FinalNetInvoiced);
        Assert.Equal(root.GetProperty("currency").GetString(), bc.FiscalLiquidation.Currency);

        // El historico edits[] tambien refleja la nueva penalty + el comentario.
        Assert.Contains("edits", bc.PartialCreditNoteApprovalRequest.Metadata!);
        Assert.Contains("Operador respondio", bc.PartialCreditNoteApprovalRequest.Metadata!);

        // El VO ComputedAt sigue coincidiendo con la columna summary (el edit no cambia
        // LiquidationComputedAt, asi que el CHECK de consistencia se mantiene).
        Assert.Equal(bc.LiquidationComputedAt, bc.FiscalLiquidation.ComputedAt);
    }

    // =========================================================================
    // 6b) EditLiquidation_TwoConsecutiveEdits_AccumulatesHistory (RH-012)
    // =========================================================================

    /// <summary>
    /// FIX A (RH-012): dos ediciones CONSECUTIVAS sobre el mismo BC tienen que
    /// ACUMULAR el historico en edits[], no pisarlo. Antes del fix, el 2do edit
    /// releia el Metadata ya serializado, donde edits[] vuelve como JsonElement
    /// (no como List&lt;object&gt;), el `is List&lt;object&gt;` daba false y edits[]
    /// se reescribia con un solo elemento => se perdia el rastro del 1er edit
    /// (auditoria fiscal rota). Este test fija que tras dos edits el array tenga
    /// LARGO 2 y que el comentario del PRIMER edit siga presente.
    ///
    /// <para>Usamos un caso reseller Factura A (mismo seed que el test 6) para caer
    /// en ManualReviewPending y poder editar. Las dos ediciones las hacen admins
    /// DISTINTOS entre si y distintos del vendedor que drafteo, para no chocar con
    /// 4-eyes (INV-FC1.3-004) ni con el bypass GR-005 (que aca esta apagado).</para>
    /// </summary>
    [Fact]
    public async Task EditLiquidation_TwoConsecutiveEdits_AccumulatesHistory()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (service, _, _) = BuildService(ctx);
        // tipoComprobante 1 = Factura A => ManualReviewPending. Reseller (default
        // TotalToCustomer): el VO FiscalLiquidation se persiste (no es CommissionOnly).
        var bcPublicId = await SeedHotelBcDraftedAsync(
            ctx, tipoComprobante: 1, importeTotal: 100_000m, vendedorUserId: "vendedor-1");

        // Confirm -> ManualReviewPending (Factura A). Lo hace el vendedor que drafteo.
        await service.ConfirmAsync(bcPublicId, BuildValidConfirm(), "vendedor-1", "Juan", false, CancellationToken.None);

        // ACT — PRIMER edit (admin-1). El comentario lleva un marcador unico
        // ("primer edit") para poder buscarlo en el JSON despues del 2do edit.
        var firstEdit = new EditLiquidationRequest(
            OperatorPenaltyAmountOverride: 10_000m,
            NonRefundableItemsAmountOverride: null,
            CreditNoteKindOverride: null,
            Comment: "primer edit: operador confirmo penalty de $10k por antelacion del cliente");
        await service.EditLiquidationAsync(bcPublicId, firstEdit, "admin-1", "Admin Uno", CancellationToken.None);

        // ACT — SEGUNDO edit (admin-2, persona distinta). El BC sigue en
        // ManualReviewPending (self-loop), asi que un segundo edit es valido.
        var secondEdit = new EditLiquidationRequest(
            OperatorPenaltyAmountOverride: 15_000m,
            NonRefundableItemsAmountOverride: null,
            CreditNoteKindOverride: null,
            Comment: "segundo edit: operador corrigio penalty a $15k tras revision del caso");
        await service.EditLiquidationAsync(bcPublicId, secondEdit, "admin-2", "Admin Dos", CancellationToken.None);

        // ASSERT — releemos el Metadata persistido y parseamos el historico edits[].
        await using var verifyCtx = _fixture.CreateDbContext();
        var bc = await verifyCtx.BookingCancellations.AsNoTracking()
            .Include(b => b.PartialCreditNoteApprovalRequest)
            .FirstAsync(b => b.PublicId == bcPublicId);

        Assert.NotNull(bc.PartialCreditNoteApprovalRequest!.Metadata);
        using var metaDoc = JsonDocument.Parse(bc.PartialCreditNoteApprovalRequest.Metadata!);
        var root = metaDoc.RootElement;

        // Core del fix: el historico tiene que tener LOS DOS edits, no uno solo.
        var edits = root.GetProperty("edits");
        Assert.Equal(JsonValueKind.Array, edits.ValueKind);
        Assert.Equal(2, edits.GetArrayLength());

        // El comentario del PRIMER edit tiene que seguir presente (no se piso).
        Assert.Contains("primer edit", bc.PartialCreditNoteApprovalRequest.Metadata!);
        // Y el del segundo tambien (sanity: ambos quedaron registrados).
        Assert.Contains("segundo edit", bc.PartialCreditNoteApprovalRequest.Metadata!);

        // Sanity extra: el top-level refleja el ULTIMO override (15k), confirmando
        // que el doble-write (RH-002) sigue coherente despues del 2do edit.
        Assert.Equal(15_000m, root.GetProperty("operatorPenaltyAmount").GetDecimal());
    }

    // =========================================================================
    // 7) CheckConstraint_SumMismatch_RejectedByPostgres
    // =========================================================================

    /// <summary>
    /// INSERT con FiscalAmountToCredit=500 + NonRefundableItemsAmount=100 +
    /// OperatorPenaltyAmount=100 != OriginalInvoiceAmount=1000 -> Postgres rechaza
    /// con SqlState 23514 -> el interceptor lo mapea a BusinessInvariantViolationException.
    /// </summary>
    [Fact]
    public async Task CheckConstraint_SumMismatch_RejectedByPostgres()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (customerId, supplierId, reservaId) = await SeedBaseAsync(ctx);
        var inv = await SeedInvoiceAsync(ctx, reservaId, 1_000m);

        var computedAt = DateTime.UtcNow;
        var bc = new BookingCancellation
        {
            ReservaId = reservaId,
            CustomerId = customerId,
            SupplierId = supplierId,
            OriginatingInvoiceId = inv,
            Status = BookingCancellationStatus.ManualReviewPending,
            Reason = "Test CHECK suma",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            CreditNoteKind = CreditNoteKind.PartialOnOriginal,
            LiquidationComputedAt = computedAt,
            // PartialCreditNoteApprovalRequestId requerido por chk_..._manualreview_approvalref
            // para Status 9. Creamos un approval minimo.
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.BCRA_A3500,
                ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS",
                FetchedAt = DateTime.UtcNow,
            },
            // Suma INVALIDA: 500 + 100 + 100 = 700 != 1000.
            FiscalLiquidation = new FiscalLiquidation
            {
                OriginalInvoiceAmount = 1_000m,
                CancellationAmount = 1_000m,
                FiscalAmountToCredit = 500m,
                NonRefundableItemsAmount = 100m,
                OperatorPenaltyAmount = 100m,
                AmountToRefundCustomer = 500m,
                FinalNetInvoiced = 500m,
                Currency = "ARS",
                ComputedAt = computedAt,
                ComputedByUserId = "vendedor-1",
            },
        };

        // El BC necesita FK al approval (CHECK manualreview_approvalref para Status 9).
        var approval = new ApprovalRequest
        {
            RequestType = ApprovalRequestType.PartialCreditNoteApproval,
            EntityType = "BookingCancellation",
            EntityId = 0,
            RequestedByUserId = "vendedor-1",
            RequestedAt = DateTime.UtcNow,
            Status = ApprovalStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "Test",
            Metadata = "{}",
        };
        ctx.ApprovalRequests.Add(approval);
        await ctx.SaveChangesAsync();
        bc.PartialCreditNoteApprovalRequestId = approval.Id;

        ctx.BookingCancellations.Add(bc);

        // ACT + ASSERT — Postgres rechaza con SqlState 23514 (interceptor lo traduce).
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ctx.SaveChangesAsync());

        var isExpected = ex is TravelApi.Domain.Exceptions.BusinessInvariantViolationException
                      || (ex is DbUpdateException dbEx
                          && dbEx.InnerException is PostgresException pgEx
                          && pgEx.SqlState == "23514");
        Assert.True(isExpected,
            $"Esperaba BusinessInvariantViolation o DbUpdateException con SqlState=23514, recibi: {ex.GetType().Name}: {ex.Message}");
    }

    // =========================================================================
    // 8) Confirm_CommissionOnly_DoesNotPersistVo (B-FISC-1)
    // =========================================================================

    /// <summary>
    /// B-FISC-1 (decision Gaston opcion A): un BC con Supplier en modo CommissionOnly.
    /// El calculator hace early-exit (STEP 0) y devuelve la terna 0+0+penalty con
    /// original>0, que NO cumple el CHECK de suma. ConfirmAsync NO debe persistir el VO
    /// (queda null) -> el CHECK no aplica, NO se rebota 23514, y el BC llega igual a
    /// ManualReviewPending con el detalle en el JSON Metadata.
    /// </summary>
    [Fact]
    public async Task Confirm_CommissionOnly_DoesNotPersistVo()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (service, _, _) = BuildService(ctx);
        // Supplier CommissionOnly: el calculator usa Supplier.InvoicingMode como modo
        // (ConfirmAsync no setea InvoicingModeAtEvent en el snapshot) -> STEP 0 early-exit.
        // tipoComprobante 6 = Factura C (sin disparar tambien Factura A; igual en
        // CommissionOnly el early-exit corre antes de evaluar el tipo de factura).
        var bcPublicId = await SeedHotelBcDraftedAsync(
            ctx, tipoComprobante: 6, importeTotal: 300_000m,
            supplierMode: SupplierInvoicingMode.CommissionOnly);

        // ACT — NO debe tirar 23514 ni ninguna excepcion.
        await service.ConfirmAsync(bcPublicId, BuildValidConfirm(), "vendedor-1", "Juan", false, CancellationToken.None);

        // ASSERT — el VO quedo null, pero el BC llego a ManualReviewPending con approval.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bc = await verifyCtx.BookingCancellations.AsNoTracking()
            .Include(b => b.PartialCreditNoteApprovalRequest)
            .FirstAsync(b => b.PublicId == bcPublicId);

        Assert.Null(bc.FiscalLiquidation);
        Assert.Equal(BookingCancellationStatus.ManualReviewPending, bc.Status);
        Assert.True(bc.ReviewRequiredReason.HasFlag(ReviewRequiredReason.InvoicingModeCommissionOnly));
        // El detalle igual viaja al JSON Metadata (para el humano que revisa).
        Assert.NotNull(bc.PartialCreditNoteApprovalRequest);
        Assert.NotNull(bc.PartialCreditNoteApprovalRequest!.Metadata);
        Assert.Contains("computedCase", bc.PartialCreditNoteApprovalRequest.Metadata!);
    }

    // =========================================================================
    // 9) Backfill_SkipsCommissionOnlyBCs (B-FISC-1)
    // =========================================================================

    /// <summary>
    /// B-FISC-1: un BC Fase 1 cuyo Metadata tiene computedCase = CommissionOnly
    /// (fiscalAmountToCredit=0, original>0). El backfill debe SALTEARLO: si poblara las
    /// columnas con esa terna, el CHECK de suma abortaria toda la migracion (23514). Tras
    /// el backfill el VO queda null y el UPDATE no rebota.
    /// </summary>
    [Fact]
    public async Task Backfill_SkipsCommissionOnlyBCs()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (customerId, supplierId, reservaId) = await SeedBaseAsync(ctx);
        var inv = await SeedInvoiceAsync(ctx, reservaId, 300_000m);

        // Metadata CommissionOnly: original 300k, fiscal 0, penalty 50k, noReemb 0.
        // 0 + 0 + 50k != 300k => si el backfill poblara columnas, el CHECK rebotaria.
        var commissionMeta = BuildMetadataJson(
            originalInvoiceAmount: 300_000m,
            fiscalAmountToCredit: 0m,
            operatorPenaltyAmount: 50_000m,
            nonRefundableItemsAmount: 0m,
            amountToRefundCustomer: 0m,
            finalNetInvoiced: 300_000m,
            computedCase: PartialCreditNoteCase.Case6_CommissionOnlyFull.ToString());

        var (bcId, _, _) = await SeedBcWithPendingApprovalAsync(
            ctx, customerId, supplierId, reservaId, inv, commissionMeta);

        // ACT — el backfill NO debe abortar ni rebotar (la fila CommissionOnly se excluye).
        await RunBackfillAsync(ctx);

        // ASSERT — el VO sigue null (no se backfilleo la fila CommissionOnly).
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.Null(bcAfter.FiscalLiquidation);
    }

    // =========================================================================
    // 10) Reject_ClearsFiscalLiquidationVo (B2)
    // =========================================================================

    /// <summary>
    /// B2 fix: al rechazar un BC (OnRejectedAsync) el reset vuelve el BC a Drafted y
    /// limpia LiquidationComputedAt. Tambien debe limpiar el owned VO
    /// (bc.FiscalLiquidation = null). Antes quedaba poblado -> "liquidacion fantasma" en
    /// un BC Drafted. Usamos un caso reseller (Factura A) donde el VO SI se persiste en
    /// Confirm, para poder verificar que el reject lo borra.
    /// </summary>
    [Fact]
    public async Task Reject_ClearsFiscalLiquidationVo()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (service, _, _) = BuildService(ctx);
        // Factura A + TotalToCustomer: Confirm persiste el VO y va a ManualReviewPending.
        var bcPublicId = await SeedHotelBcDraftedAsync(ctx, tipoComprobante: 1, importeTotal: 100_000m);

        await service.ConfirmAsync(bcPublicId, BuildValidConfirm(), "vendedor-1", "Juan", false, CancellationToken.None);

        // Precondicion: el VO quedo poblado tras el Confirm.
        int approvalId;
        await using (var preCtx = _fixture.CreateDbContext())
        {
            var preBc = await preCtx.BookingCancellations.AsNoTracking()
                .FirstAsync(b => b.PublicId == bcPublicId);
            Assert.NotNull(preBc.FiscalLiquidation);
            Assert.NotNull(preBc.PartialCreditNoteApprovalRequestId);
            approvalId = preBc.PartialCreditNoteApprovalRequestId!.Value;
        }

        // ACT — rechazar (resolverNotes >= 20 chars, distinto del solicitante).
        await service.OnRejectedAsync(
            approvalId,
            resolverUserId: "admin-revisor",
            resolverUserName: "Admin Revisor",
            resolverNotes: "Rechazado: la liquidacion no corresponde, reformular la operacion.",
            ct: CancellationToken.None);

        // ASSERT — el BC volvio a Drafted y el VO quedo null (sin liquidacion fantasma).
        await using var verifyCtx = _fixture.CreateDbContext();
        var bc = await verifyCtx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bcPublicId);

        Assert.Equal(BookingCancellationStatus.Drafted, bc.Status);
        Assert.Null(bc.FiscalLiquidation);
        Assert.Null(bc.LiquidationComputedAt);
    }
}
