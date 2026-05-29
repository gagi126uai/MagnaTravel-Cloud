using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.1 (ADR-009 §6.1, 2026-05-21): tests unit del clasificador fiscal puro.
///
/// <para><b>Sin DB, sin async, sin TestContainers</b>: el calculator es puro y
/// recibe entidades en memoria. Cada test debe correr en pocos milisegundos.</para>
///
/// <para>Cubre los 24 tests del ADR §6.1:
///  <list type="bullet">
///   <item>Los 8 casos de la matriz del contador.</item>
///   <item>Los 2 escenarios concretos (factura A $1M con retencion / comision $100k).</item>
///   <item>Edge cases: regex malformed, JSON penalty malformed, monto 0, currency != ARS.</item>
///   <item>STEP 0 short-circuit CommissionOnly (GR-003).</item>
///   <item>STEP 3 flag PenaltyResetUncertainInResellerMode (GR-006).</item>
///   <item>RH-008: defaults vacios NO disparan OriginalInvoiceUnclear / LegacyInvoice.</item>
///  </list>
/// </para>
/// </summary>
public class FiscalLiquidationCalculatorTests
{
    // ============================================================
    // Helpers: construyen entidades sanas con defaults conservadores.
    // Cada test override solo lo que le interesa, asi se lee el caso
    // sin ruido de boilerplate.
    // ============================================================

    /// <summary>Settings con defaults productivos (heuristicas RH-008 OFF).</summary>
    private static OperationalFinanceSettings DefaultSettings() => new()
    {
        EnablePartialCreditNotes = true,
        PartialNcAutoApprovalThreshold = 500_000m,
        PartialNcAdminReviewThreshold = 2_000_000m,
        PartialNcAccountingReviewThreshold = null,
        GenericDescriptionPatterns = string.Empty, // RH-008: OFF por default
        Fc13DeployDate = null,                      // RH-008: OFF por default
    };

    /// <summary>Factura B/C estandar ($100k) con un solo item Service refundable.</summary>
    private static (Invoice invoice, List<InvoiceItem> items) FacturaBC(
        decimal total = 100_000m,
        int tipo = 6,
        DateTime? createdAt = null,
        int? originalInvoiceId = null)
    {
        var invoice = new Invoice
        {
            Id = 1,
            TipoComprobante = tipo,
            ImporteTotal = total,
            ImporteNeto = total / 1.21m,
            ImporteIva = total - (total / 1.21m),
            CreatedAt = createdAt ?? DateTime.UtcNow,
            OriginalInvoiceId = originalInvoiceId,
        };

        var items = new List<InvoiceItem>
        {
            new()
            {
                Id = 10,
                InvoiceId = 1,
                Description = "Hotel 5 noches",
                Total = total,
                ImporteIva = invoice.ImporteIva,
                IsRefundable = true,
                ItemCategory = InvoiceItemCategory.Service,
                SourceServicioReservaId = 100,
            },
        };

        return (invoice, items);
    }

    private static Supplier DefaultSupplier(SupplierInvoicingMode mode = SupplierInvoicingMode.TotalToCustomer)
        => new()
        {
            Id = 50,
            Name = "Operador SRL",
            InvoicingMode = mode,
            PenaltyPolicyJson = null,
        };

    /// <summary>Logger silencioso para tests donde no nos importan los warnings.</summary>
    private static ILogger<FiscalLiquidationCalculator> SilentLogger
        => NullLogger<FiscalLiquidationCalculator>.Instance;

    /// <summary>Logger que captura los entries en memoria — usado para verificar warnings.</summary>
    private sealed class CapturingLogger : ILogger<FiscalLiquidationCalculator>
    {
        public List<(LogLevel level, string message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    // ============================================================
    // CASO 1 — cancelacion parcial sin penalidad, debajo del threshold
    // ============================================================

    [Fact]
    public void Calculate_Case1_PartialNoPenalty_BelowThreshold_ReturnsAutoApprovable()
    {
        // Setup: factura $100k Tipo B, cancelacion parcial de $30k, sin penalty.
        // Resultado esperado: caso 1, sin disparadores, auto-approvable.
        var (invoice, items) = FacturaBC(total: 100_000m);
        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 30_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var calculator = new FiscalLiquidationCalculator(SilentLogger);
        var result = calculator.Calculate(input, DefaultSettings());

        Assert.Equal(PartialCreditNoteCase.Case1_PartialCancellationNoRetention, result.Case);
        Assert.Equal(CreditNoteKind.PartialOnOriginal, result.Kind);
        Assert.Equal(ReviewRequiredReason.None, result.ReviewRequiredReason);
        Assert.Equal(100_000m, result.FiscalAmountToCredit);
        Assert.Equal(100_000m, result.AmountToRefundCustomer);
        Assert.Equal(0m, result.FinalNetInvoiced);
    }

    // ============================================================
    // CASO 2 — cancelacion total sin retencion
    // ============================================================

    [Fact]
    public void Calculate_Case2_FullCancellationNoRetention_BelowThreshold_ReturnsAutoApprovable()
    {
        var (invoice, items) = FacturaBC(total: 200_000m);
        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 200_000m,
            CancellationAmount: 200_000m, // = total => cancelacion total
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.Equal(PartialCreditNoteCase.Case2_FullCancellationNoRetention, result.Case);
        Assert.Equal(CreditNoteKind.PartialOnOriginal, result.Kind);
        Assert.Equal(ReviewRequiredReason.None, result.ReviewRequiredReason);
        Assert.Equal(200_000m, result.FiscalAmountToCredit);
    }

    // ============================================================
    // CASO 3 — cancelacion total con penalidad operador (GR-006)
    // ============================================================

    [Fact]
    public void Calculate_Case3_FullWithPenalty_TotalToCustomer_FlagsPenaltyResetUncertain()
    {
        // GR-006: en modo TotalToCustomer con penalty > 0 SIEMPRE flageamos
        // PenaltyResetUncertainInResellerMode hasta respuesta F4 contador.
        var (invoice, items) = FacturaBC(total: 100_000m);
        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 30_000m, // penalty > 0
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.Equal(PartialCreditNoteCase.Case3_FullCancellationWithPenalty, result.Case);
        Assert.True(result.ReviewRequiredReason.HasFlag(
            ReviewRequiredReason.PenaltyResetUncertainInResellerMode));
        Assert.Equal(70_000m, result.FiscalAmountToCredit); // 100k - 30k penalty
    }

    [Fact]
    public void Calculate_Case3_FullWithPenalty_AboveAdminThreshold_ReturnsRequiresReview()
    {
        // Factura $3M, penalty $200k => fiscal $2.8M (> 2M threshold admin) =>
        // dispara AmountAboveAdminThreshold ADEMAS de PenaltyResetUncertain.
        var (invoice, items) = FacturaBC(total: 3_000_000m);
        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 3_000_000m,
            CancellationAmount: 3_000_000m,
            OperatorPenaltyAmount: 200_000m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.Equal(PartialCreditNoteCase.Case3_FullCancellationWithPenalty, result.Case);
        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.AmountAboveAdminThreshold));
        Assert.True(result.ReviewRequiredReason.HasFlag(
            ReviewRequiredReason.PenaltyResetUncertainInResellerMode));
    }

    // ============================================================
    // CASO 4 — factura confusa (heuristicas RH-008)
    // ============================================================

    [Fact]
    public void Calculate_Case4_GenericDescriptionMatchesPattern_WhenSettingEnabled()
    {
        // Setting habilitado: regex matchea descripcion generica => flag.
        var (invoice, items) = FacturaBC(total: 50_000m);
        items[0].Description = "servicio general"; // matchea regex de abajo

        var settings = DefaultSettings();
        settings.GenericDescriptionPatterns = "^(servicio|concepto|importe)";

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 50_000m,
            CancellationAmount: 50_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, settings);

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.OriginalInvoiceUnclear));
        Assert.Equal(PartialCreditNoteCase.Case4_OriginalInvoiceUnclear, result.Case);
        Assert.Equal(CreditNoteKind.TotalPlusNewInvoice, result.Kind);
    }

    [Fact]
    public void Calculate_Case4_GenericDescriptionMatchesPattern_WhenSettingDisabled_Default()
    {
        // RH-008: setting default vacio => heuristica NO se evalua => no flag.
        var (invoice, items) = FacturaBC(total: 50_000m);
        items[0].Description = "servicio general"; // matchearia si el regex estuviera activo

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 50_000m,
            CancellationAmount: 50_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings()); // default GenericDescriptionPatterns=""

        Assert.False(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.OriginalInvoiceUnclear));
        Assert.NotEqual(PartialCreditNoteCase.Case4_OriginalInvoiceUnclear, result.Case);
    }

    [Fact]
    public void Calculate_Case4_ManualCheckboxOriginalInvoiceUnclear_AlwaysFlags()
    {
        // El checkbox manual del vendedor SIEMPRE dispara — independiente del setting.
        var (invoice, items) = FacturaBC(total: 50_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 50_000m,
            CancellationAmount: 50_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: true); // vendedor tildo manual

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings()); // settings default vacios

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.OriginalInvoiceUnclear));
        Assert.Equal(PartialCreditNoteCase.Case4_OriginalInvoiceUnclear, result.Case);
        Assert.Equal(CreditNoteKind.TotalPlusNewInvoice, result.Kind);
    }

    // ============================================================
    // CASO 4 — sub-heuristicas adicionales (RH-101)
    // ------------------------------------------------------------
    // Las heuristicas del STEP 2 son tres: (1) descripcion generica, (2) items
    // sin trazabilidad al servicio origen, (3) suma de IVA por linea desalineada
    // vs el IVA total de la factura. La #1 ya tiene cobertura arriba — estos
    // dos tests agregan cobertura para #2 y #3.
    // ============================================================

    [Fact]
    public void Calculate_Case4_HalfItemsWithoutSource_FlagsOriginalInvoiceUnclear()
    {
        // Sub-heuristica 2: > 50% del total de la factura corresponde a items
        // con SourceServicioReservaId=null (no sabemos a que reserva pertenecen).
        // El setting esta activo (regex con un pattern que NO matchea las
        // descripciones) para que solo dispare por la heuristica 2, no por la 1.
        var (invoice, items) = FacturaBC(total: 100_000m);

        // Item original: lo dejamos sin source y le ponemos 60% del total.
        items[0].SourceServicioReservaId = null;
        items[0].Total = 60_000m;
        items[0].Description = "alpha"; // no matchea el pattern de abajo

        // Segundo item: SI tiene source, $40k. Total factura = 100k.
        items.Add(new InvoiceItem
        {
            Id = 11,
            InvoiceId = 1,
            Description = "beta", // no matchea el pattern de abajo
            Total = 40_000m,
            ImporteIva = 6_942m,
            IsRefundable = true,
            SourceServicioReservaId = 100,
        });
        // 60k sin source / 100k total = 60% => dispara la sub-heuristica 2.

        var settings = DefaultSettings();
        // Pattern que NO matchea ninguna descripcion — activa STEP 2 pero descarta
        // la sub-heuristica 1, asi quedamos seguros que el flag viene de la #2.
        settings.GenericDescriptionPatterns = "^xyz_no_match";

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger).Calculate(input, settings);

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.OriginalInvoiceUnclear));
        Assert.Equal(PartialCreditNoteCase.Case4_OriginalInvoiceUnclear, result.Case);
    }

    [Fact]
    public void Calculate_Case4_ItemIvaDoesNotMatchInvoiceIva_FlagsOriginalInvoiceUnclear()
    {
        // Sub-heuristica 3: la suma de ImporteIva de los items no cierra con
        // Invoice.ImporteIva (tolerancia $0.50). Indica datos inconsistentes en
        // la factura origen — derivamos a manual review.
        var (invoice, items) = FacturaBC(total: 100_000m);

        // Factura: ImporteIva $1.000. Item: $17.355. Diferencia >> $0.50 => dispara.
        invoice.ImporteIva = 1_000m;
        items[0].ImporteIva = 17_355m;
        items[0].Description = "alpha"; // no matchea el pattern de abajo

        var settings = DefaultSettings();
        // Pattern que NO matchea — habilita STEP 2 sin disparar sub-heuristica 1.
        settings.GenericDescriptionPatterns = "^xyz_no_match";

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger).Calculate(input, settings);

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.OriginalInvoiceUnclear));
        Assert.Equal(PartialCreditNoteCase.Case4_OriginalInvoiceUnclear, result.Case);
    }

    // ============================================================
    // NC EN CADENA (RH-102) — la factura origen ya es una NC
    // ------------------------------------------------------------
    // Caso raro pero existente: una factura registrada en BD es en realidad una
    // NC que apunta a OTRA factura (OriginalInvoiceId != null). El STEP 1 dispara
    // ReviewRequiredReason.Other porque el calculator no esta preparado para
    // resolver cadenas — manual review obligatorio.
    // ============================================================

    [Fact]
    public void Calculate_OriginatingInvoiceIsItselfACreditNote_FlagsOtherReason()
    {
        // Factura origen ya es NC apuntando a otra factura ($999). Esperamos
        // el flag Other y NO otros disparadores (la factura es B/C sin penalty,
        // monto chico, ARS, mode TotalToCustomer).
        var (invoice, items) = FacturaBC(total: 100_000m, originalInvoiceId: 999);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger).Calculate(input, DefaultSettings());

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.Other));
    }

    // ============================================================
    // CASO 5 — CommissionOnly partial (GR-003)
    // ============================================================

    [Fact]
    public void Calculate_Case5_CommissionOnly_PartialReturn_FlagsCommissionOnly()
    {
        // GR-003: STEP 0 early exit. Cancelacion parcial en modo CommissionOnly.
        var (invoice, items) = FacturaBC(total: 100_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(SupplierInvoicingMode.CommissionOnly),
            InvoicingModeAtEvent: SupplierInvoicingMode.CommissionOnly,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 30_000m, // parcial
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.Equal(PartialCreditNoteCase.Case5_CommissionOnlyPartial, result.Case);
        Assert.Equal(CreditNoteKind.PartialOnOriginal, result.Kind);
        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.InvoicingModeCommissionOnly));
        // STEP 0 early exit: NO calcula formula => FiscalAmountToCredit queda en 0.
        Assert.Equal(0m, result.FiscalAmountToCredit);
        Assert.Equal(0m, result.AmountToRefundCustomer);
        Assert.Equal(100_000m, result.FinalNetInvoiced);
    }

    // ============================================================
    // CASO 6 — CommissionOnly full (GR-003)
    // ============================================================

    [Fact]
    public void Calculate_Case6_CommissionOnly_FullReturn_FlagsCommissionOnly()
    {
        var (invoice, items) = FacturaBC(total: 100_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(SupplierInvoicingMode.CommissionOnly),
            InvoicingModeAtEvent: SupplierInvoicingMode.CommissionOnly,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m, // total
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.Equal(PartialCreditNoteCase.Case6_CommissionOnlyFull, result.Case);
        Assert.Equal(CreditNoteKind.PartialOnOriginal, result.Kind);
        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.InvoicingModeCommissionOnly));
    }

    /// <summary>GR-003: STEP 0 cortocircuita ANTES de cualquier flag de monto/factura A/etc.</summary>
    [Fact]
    public void Calculate_CommissionOnly_EarlyExit_NoComputationOfPenaltyFormula()
    {
        // Mismo input que un caso 3 enorme — pero CommissionOnly debe cortar antes.
        // Verificamos que NO se calcula la formula (FiscalAmountToCredit = 0) y NO
        // se agrega flag de penalty, threshold ni factura A.
        var (invoice, items) = FacturaBC(total: 5_000_000m, tipo: 1); // Factura A grande
        items[0].IsRefundable = false; // tendria HasNonRefundableItems si calculara

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(SupplierInvoicingMode.CommissionOnly),
            InvoicingModeAtEvent: SupplierInvoicingMode.CommissionOnly,
            OriginalInvoiceAmount: 5_000_000m,
            CancellationAmount: 5_000_000m,
            OperatorPenaltyAmount: 500_000m, // penalty grande
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        // El UNICO flag debe ser InvoicingModeCommissionOnly. NO debe haber
        // AmountAboveAdminThreshold ni HasNonRefundableItems ni Factura A.
        Assert.Equal(ReviewRequiredReason.InvoicingModeCommissionOnly, result.ReviewRequiredReason);
        Assert.Equal(0m, result.FiscalAmountToCredit);
    }

    // ============================================================
    // CASO 7 — Modo cambio entre snapshot y actual (RetentionChangesNature)
    // ============================================================

    [Fact]
    public void Calculate_Case7_InvoicingModeChanged_ReturnsRequiresReview()
    {
        // Snapshot TotalToCustomer al momento de facturar, pero Supplier ahora CommissionOnly.
        // STEP 0 usa el snapshot (TotalToCustomer) asi que NO cortocircuita.
        // STEP 1 dispara RetentionChangesNature porque el modo cambio.
        var (invoice, items) = FacturaBC(total: 100_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(SupplierInvoicingMode.CommissionOnly), // actual
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,     // snapshot al facturar
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.RetentionChangesNature));
        Assert.Equal(PartialCreditNoteCase.Case7_RetentionChangesNature, result.Case);
        Assert.Equal(CreditNoteKind.TotalPlusNewInvoice, result.Kind);
    }

    // ============================================================
    // CASO 8 — Factura A (cliente RI)
    // ============================================================

    [Fact]
    public void Calculate_Case8_FacturaA_AnyAmount_ReturnsRequiresReview()
    {
        // Factura A, cualquier monto chico => SIEMPRE flag CustomerIsRiOrFacturaA.
        var (invoice, items) = FacturaBC(total: 50_000m, tipo: 1); // Factura A

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 50_000m,
            CancellationAmount: 50_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.CustomerIsRiOrFacturaA));
        Assert.Equal(PartialCreditNoteCase.Case8_FacturaA, result.Case);
        Assert.Equal(CreditNoteKind.PartialOnOriginal, result.Kind);
    }

    [Fact]
    public void Calculate_FacturaA_PlusNonRefundable_ReturnsTwoFlags()
    {
        // Escenario A del contador: Factura A $1M con $250k retenido (no reintegrable).
        // Resultado: dos flags (CustomerIsRiOrFacturaA + HasNonRefundableItems).
        var (invoice, items) = FacturaBC(total: 1_000_000m, tipo: 1);
        items.Add(new InvoiceItem
        {
            Id = 11,
            InvoiceId = 1,
            Description = "Cargo gestion",
            Total = 250_000m,
            ImporteIva = 250_000m * 0.21m / 1.21m,
            IsRefundable = false,
            ItemCategory = InvoiceItemCategory.AdministrativeFee,
        });
        // Ajustamos el total para que la suma cierre conceptualmente: la factura
        // ahora es $1.250.000 (item 1 de 1M + item 2 de 250k).
        invoice.ImporteTotal = 1_250_000m;

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 1_250_000m,
            CancellationAmount: 1_250_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.CustomerIsRiOrFacturaA));
        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.HasNonRefundableItems));
        Assert.Equal(PartialCreditNoteCase.Case8_FacturaA, result.Case);
        Assert.Equal(250_000m, result.NonRefundableItemsAmount);
        Assert.Equal(1_000_000m, result.FiscalAmountToCredit); // 1.25M - 250k no refundable
        Assert.Equal(250_000m, result.FinalNetInvoiced);
    }

    [Fact]
    public void Calculate_FacturaA_PlusCommissionOnly_BothFlagsSet()
    {
        // Caso raro: Factura A en operador CommissionOnly. STEP 0 cortocircuita
        // ANTES de evaluar Factura A => unico flag debe ser InvoicingModeCommissionOnly.
        // (Documenta comportamiento: el contador resuelve esto en manual review).
        var (invoice, items) = FacturaBC(total: 1_000_000m, tipo: 1);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(SupplierInvoicingMode.CommissionOnly),
            InvoicingModeAtEvent: SupplierInvoicingMode.CommissionOnly,
            OriginalInvoiceAmount: 1_000_000m,
            CancellationAmount: 1_000_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        // STEP 0 corta antes de evaluar Factura A — solo CommissionOnly se setea.
        Assert.Equal(ReviewRequiredReason.InvoicingModeCommissionOnly, result.ReviewRequiredReason);
        Assert.Equal(PartialCreditNoteCase.Case6_CommissionOnlyFull, result.Case);
    }

    // ============================================================
    // LEGACY INVOICE — RH-008 (default OFF)
    // ============================================================

    [Fact]
    public void Calculate_LegacyInvoice_WhenSettingEnabled_Flags()
    {
        // Setting Fc13DeployDate activo + factura vieja => flag LegacyInvoice.
        var oldInvoiceDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var deployDate = new DateTime(2026, 5, 21, 0, 0, 0, DateTimeKind.Utc);
        var (invoice, items) = FacturaBC(total: 100_000m, createdAt: oldInvoiceDate);

        var settings = DefaultSettings();
        settings.Fc13DeployDate = deployDate;
        settings.GenericDescriptionPatterns = "^(servicio|concepto)"; // requerido para evaluar legacy

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, settings);

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.LegacyInvoice));
    }

    [Fact]
    public void Calculate_LegacyInvoice_WhenSettingDisabled_Default_DoesNotFlag()
    {
        // RH-008: Fc13DeployDate=null (default) => NUNCA flag legacy.
        var oldInvoiceDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (invoice, items) = FacturaBC(total: 100_000m, createdAt: oldInvoiceDate);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings()); // Fc13DeployDate=null

        Assert.False(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.LegacyInvoice));
    }

    // ============================================================
    // MULTI-CURRENCY
    // ============================================================

    [Fact]
    public void Calculate_MultiCurrency_ReturnsRequiresReview()
    {
        var (invoice, items) = FacturaBC(total: 1_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 1_000m,
            CancellationAmount: 1_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false,
            Currency: "USD"); // distinto de ARS

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.MultiCurrency));
        Assert.Equal("USD", result.Currency);
    }

    // ------------------------------------------------------------
    // FC1.3.F2.5 (multimoneda, 2026-05-28): el flag maestro cambia el criterio
    // de MultiCurrency. Estos dos tests blindan las dos ramas.
    // ------------------------------------------------------------

    /// <summary>
    /// F2.5 path ON: con el flag maestro prendido, una factura en moneda extranjera
    /// (USD) YA NO se manda a revision manual por la moneda. El pipeline sabe emitir la
    /// NC en la moneda y cotizacion del comprobante origen, asi que MultiCurrency deja de
    /// ser disparador automatico.
    /// </summary>
    [Fact]
    public void Calculator_MultiCurrencyWithFase2On_DoesNotFlagReason()
    {
        var (invoice, items) = FacturaBC(total: 1_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 1_000m,
            CancellationAmount: 1_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false,
            Currency: "USD"); // moneda extranjera

        // Settings con el flag maestro de Fase 2 PRENDIDO.
        var settings = DefaultSettings();
        settings.EnablePartialCreditNoteRealEmission = true;

        var result = new FiscalLiquidationCalculator(SilentLogger).Calculate(input, settings);

        // La moneda extranjera NO debe disparar revision manual cuando F2.5 esta ON.
        Assert.False(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.MultiCurrency));
        Assert.Equal("USD", result.Currency);
    }

    /// <summary>
    /// F2.5 path OFF (back-compat Fase 1): con el flag maestro apagado (estado actual de
    /// prod), una factura en USD SIGUE yendo a revision manual por la moneda. Garantiza
    /// que el comportamiento de Fase 1 queda intacto mientras el flag este OFF.
    /// </summary>
    [Fact]
    public void Calculator_MultiCurrencyWithFase2Off_StillFlagsReason()
    {
        var (invoice, items) = FacturaBC(total: 1_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 1_000m,
            CancellationAmount: 1_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false,
            Currency: "USD");

        // Settings con el flag maestro de Fase 2 APAGADO (default).
        var settings = DefaultSettings();
        settings.EnablePartialCreditNoteRealEmission = false;

        var result = new FiscalLiquidationCalculator(SilentLogger).Calculate(input, settings);

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.MultiCurrency));
        Assert.Equal("USD", result.Currency);
    }

    /// <summary>
    /// FIX B-1 (revision backend+contable, 2026-05-28): una moneda extranjera que el sistema NO
    /// sabe mapear a un codigo ARCA (ej. EUR — solo soportamos ARS+USD) SIEMPRE va a revision
    /// manual, incluso con el flag maestro PRENDIDO.
    ///
    /// <para>Por que este test es critico: antes del fix, el criterio era "isForeign &amp;&amp; !flag".
    /// Con el flag ON, EUR dejaba de disparar MultiCurrency y la liquidacion quedaba auto-aprobable
    /// — y mas adelante el path de NC total la emitia en PESOS por default (un EUR como un peso).
    /// El fix alinea el calculator con el emisor (ArcaCurrencyMapper): moneda no soportada =
    /// manual SIEMPRE, sin importar el flag. Nunca auto-emite ni rompe con throw a mitad de camino.</para>
    /// </summary>
    [Fact]
    public void Calculator_UnsupportedCurrency_FlagsMultiCurrency_EvenWithFlagOn()
    {
        var (invoice, items) = FacturaBC(total: 1_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 1_000m,
            CancellationAmount: 1_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false,
            Currency: "EUR"); // moneda extranjera NO soportada por el mapeo ARCA

        // Flag maestro de Fase 2 PRENDIDO: aun asi, EUR debe ir a revision manual.
        var settings = DefaultSettings();
        settings.EnablePartialCreditNoteRealEmission = true;

        var result = new FiscalLiquidationCalculator(SilentLogger).Calculate(input, settings);

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.MultiCurrency));
        Assert.Equal("EUR", result.Currency);
    }

    // ============================================================
    // INV-FC1.3-005: tolerancia + violacion
    // ============================================================

    [Fact]
    public void Calculate_SumValidation_BreaksTolerance_ThrowsInvariantViolation()
    {
        // Penalty $200k + items no refundable $900k > total $1M => violacion INV-FC1.3-005.
        var (invoice, items) = FacturaBC(total: 1_000_000m);
        items[0].IsRefundable = false;
        items[0].Total = 900_000m;

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 1_000_000m,
            CancellationAmount: 1_000_000m,
            OperatorPenaltyAmount: 200_000m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var calculator = new FiscalLiquidationCalculator(SilentLogger);

        var ex = Assert.Throws<BusinessInvariantViolationException>(
            () => calculator.Calculate(input, DefaultSettings()));
        Assert.Equal("INV-FC1.3-005", ex.InvariantCode);
    }

    // ============================================================
    // STEP 4: thresholds de monto
    // ============================================================

    [Fact]
    public void Calculate_AccountingThresholdNull_DoesNotFlagAccountingReview()
    {
        // Threshold accounting null => NUNCA dispara AmountAboveAccountingThreshold.
        var (invoice, items) = FacturaBC(total: 10_000_000m); // monto gigante

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 10_000_000m,
            CancellationAmount: 10_000_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var settings = DefaultSettings();
        settings.PartialNcAccountingReviewThreshold = null;

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, settings);

        Assert.False(result.ReviewRequiredReason.HasFlag(
            ReviewRequiredReason.AmountAboveAccountingThreshold));
        // Pero si dispara admin threshold (10M > 2M default).
        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.AmountAboveAdminThreshold));
    }

    [Fact]
    public void Calculate_AmountAboveAccountingThreshold_FlagsAccountingReview()
    {
        var (invoice, items) = FacturaBC(total: 10_000_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 10_000_000m,
            CancellationAmount: 10_000_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var settings = DefaultSettings();
        settings.PartialNcAccountingReviewThreshold = 5_000_000m; // 10M > 5M

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, settings);

        Assert.True(result.ReviewRequiredReason.HasFlag(
            ReviewRequiredReason.AmountAboveAccountingThreshold));
        // El else-if NO dispara AmountAboveAdminThreshold cuando accounting ya disparo.
        Assert.False(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.AmountAboveAdminThreshold));
    }

    // ============================================================
    // NARRATIVA
    // ============================================================

    [Fact]
    public void Calculate_ExplanationContainsCaseName_AndReasonFlags()
    {
        var (invoice, items) = FacturaBC(total: 50_000m, tipo: 1); // Factura A

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 50_000m,
            CancellationAmount: 50_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.Contains("Case8_FacturaA", result.ClassificationExplanation);
        Assert.Contains("CustomerIsRiOrFacturaA", result.ClassificationExplanation);
    }

    // ============================================================
    // STEP 3 negativo: TotalToCustomer SIN penalty no dispara GR-006
    // ============================================================

    [Fact]
    public void Calculate_TotalToCustomer_NoPenalty_NoFlagPenaltyResetUncertain()
    {
        var (invoice, items) = FacturaBC(total: 100_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 0m, // sin penalty
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.False(result.ReviewRequiredReason.HasFlag(
            ReviewRequiredReason.PenaltyResetUncertainInResellerMode));
    }

    // ============================================================
    // FALLBACKS: regex malformed + JSON malformed
    // ============================================================

    [Fact]
    public void Calculate_GenericDescriptionPatternsMalformedRegex_FallsBackGracefully()
    {
        // Regex malformado (parentesis sin cerrar) => log warning + skip heuristica.
        var (invoice, items) = FacturaBC(total: 50_000m);
        items[0].Description = "servicio";

        var settings = DefaultSettings();
        settings.GenericDescriptionPatterns = "^(servicio|"; // malformed: parentesis sin cerrar

        var logger = new CapturingLogger();
        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 50_000m,
            CancellationAmount: 50_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(logger).Calculate(input, settings);

        // No tira. Skipea heuristica => OriginalInvoiceUnclear NO se setea.
        Assert.False(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.OriginalInvoiceUnclear));
        // Log warning emitido.
        Assert.Contains(logger.Entries, e =>
            e.level == LogLevel.Warning && e.message.Contains("regex malformado"));
    }

    [Fact]
    public void Calculate_SupplierPenaltyPolicyJsonMalformed_FallsBackToManualInput()
    {
        // JSON malformado en la tabla del operador => log warning + sigue con el monto
        // manual del vendedor. NO tira.
        var (invoice, items) = FacturaBC(total: 100_000m);
        var supplier = DefaultSupplier();
        supplier.PenaltyPolicyJson = "{tiers: invalid_json"; // JSON malformed

        var logger = new CapturingLogger();
        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: supplier,
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 15_000m, // vendedor ingreso manual => calculator usa este
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(logger).Calculate(input, DefaultSettings());

        // El calculator NO tira; usa los $15k manuales del vendedor.
        Assert.Equal(15_000m, result.OperatorPenaltyAmount);
        Assert.Equal(85_000m, result.FiscalAmountToCredit); // 100k - 15k
        Assert.Contains(logger.Entries, e =>
            e.level == LogLevel.Warning && e.message.Contains("PenaltyPolicyJson malformado"));
    }

    // ============================================================
    // EDGE CASE: input con monto 0
    // ============================================================

    [Fact]
    public void Calculate_ZeroAmounts_ReturnsZeroLiquidationCase2()
    {
        // Factura $0 (caso degenerado pero posible si vendedor cargo mal).
        // Esperamos que NO tire y devuelva caso 2 (total) sin disparadores.
        var (invoice, items) = FacturaBC(total: 0m);
        items[0].Total = 0m;
        items[0].ImporteIva = 0m;
        invoice.ImporteIva = 0m;

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 0m,
            CancellationAmount: 0m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.Equal(0m, result.FiscalAmountToCredit);
        Assert.Equal(0m, result.AmountToRefundCustomer);
        Assert.Equal(ReviewRequiredReason.None, result.ReviewRequiredReason);
        Assert.Equal(PartialCreditNoteCase.Case2_FullCancellationNoRetention, result.Case);
    }

    // ============================================================
    // ESCENARIO B contador: comision $100k con $50k retenido
    // ============================================================

    [Fact]
    public void Calculate_ScenarioB_CommissionWithRetention_StaysInCommissionOnlyShortCircuit()
    {
        // Escenario B contador: operador CommissionOnly, factura $100k de comision,
        // operador retiene $50k. STEP 0 cortocircuita => manual review obligatorio.
        // (En manual review el admin define la liquidacion exacta).
        var (invoice, items) = FacturaBC(total: 100_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(SupplierInvoicingMode.CommissionOnly),
            InvoicingModeAtEvent: SupplierInvoicingMode.CommissionOnly,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 50_000m, // operador retiene
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.InvoicingModeCommissionOnly));
        Assert.Equal(PartialCreditNoteCase.Case6_CommissionOnlyFull, result.Case);
        // Verificamos que STEP 0 corto: NO se proceso la penalty en la formula.
        Assert.Equal(0m, result.FiscalAmountToCredit);
    }

    // ============================================================
    // LEGACY: snapshot null + supplier cambio modo => usa actual
    // ============================================================

    [Fact]
    public void Calculator_LegacyInvoiceWithNullSnapshot_AndSupplierModeChanged_UsesNullDefaultBehavior()
    {
        // Factura legacy sin snapshot del modo. Supplier actual = CommissionOnly.
        // Esperado: usa el actual (CommissionOnly) como fallback => STEP 0 cortocircuita.
        var (invoice, items) = FacturaBC(total: 100_000m);

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(SupplierInvoicingMode.CommissionOnly), // actual
            InvoicingModeAtEvent: null,                                       // legacy: sin snapshot
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 100_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        // Fallback al actual (CommissionOnly) => short-circuit GR-003.
        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.InvoicingModeCommissionOnly));
        Assert.Equal(PartialCreditNoteCase.Case6_CommissionOnlyFull, result.Case);
    }

    // ============================================================
    // FC1.3 Fase 2 (G-F2-C, 2026-05-22): factura con tributos provinciales
    // ------------------------------------------------------------
    // El calculator detecta Invoice.Tributes.Any() == true y agrega el flag
    // HasProvincialTributes para que el caller derive a manual review. No se
    // toca el calculo de montos — el flag es aditivo. Cualquier factura B/C
    // estandar (sin tributos) NO debe llevar este flag (regresion del flujo
    // feliz de Fase 1).
    // ============================================================

    [Fact]
    public void Calculator_InvoiceWithTributes_AddsHasProvincialTributesFlag()
    {
        // Setup: factura B estandar pero con un IIBB Capital de $100 agregado.
        // Resultado esperado: el flag HasProvincialTributes esta activo, el caso
        // sigue clasificandose normal (1 — parcial sin penalty) y los montos no
        // cambian. La derivacion a manual review la hace el caller mirando el flag.
        var (invoice, items) = FacturaBC(total: 100_000m);
        invoice.Tributes = new List<InvoiceTribute>
        {
            new()
            {
                Id = 500,
                InvoiceId = invoice.Id,
                TributeId = 99, // 99 = IIBB en la tabla AFIP
                Description = "IIBB Capital",
                BaseImponible = 100_000m,
                Alicuota = 0.10m,
                Importe = 100m,
            },
        };

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 30_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        // El flag nuevo debe estar prendido.
        Assert.True(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.HasProvincialTributes));

        // El calculo de montos NO cambia (el flag es aditivo, no afecta la formula).
        Assert.Equal(100_000m, result.FiscalAmountToCredit);
    }

    [Fact]
    public void Calculator_InvoiceWithoutTributes_DoesNotAddHasProvincialTributesFlag()
    {
        // Regresion del flujo feliz Fase 1: factura B estandar sin tributos NO
        // debe llevar el flag nuevo. Sin esto, todas las cancelaciones Hotel
        // empezarian a caer a manual review post-deploy de F2.0 (catastrofe).
        var (invoice, items) = FacturaBC(total: 100_000m);
        // Default constructor de Invoice ya pone Tributes = new List<>() (vacio).
        // Lo dejamos explicito para que el test sea legible sin tener que mirar
        // el helper.
        invoice.Tributes = new List<InvoiceTribute>();

        var input = new FiscalLiquidationInput(
            OriginatingInvoice: invoice,
            Items: items,
            Supplier: DefaultSupplier(),
            InvoicingModeAtEvent: SupplierInvoicingMode.TotalToCustomer,
            OriginalInvoiceAmount: 100_000m,
            CancellationAmount: 30_000m,
            OperatorPenaltyAmount: 0m,
            RetentionNatureChangedByUser: false,
            OriginalInvoiceUnclearByUser: false);

        var result = new FiscalLiquidationCalculator(SilentLogger)
            .Calculate(input, DefaultSettings());

        Assert.False(result.ReviewRequiredReason.HasFlag(ReviewRequiredReason.HasProvincialTributes));
    }
}
