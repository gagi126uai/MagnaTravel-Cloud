using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.2.2 v3 §8 (2026-05-18): tests integration de
/// <c>OperatorRefundService</c> contra Postgres real. Cubre:
/// <list type="bullet">
///   <item>RecordReceived (TG4 INV-CONT-09: linkear ManualCashMovement).</item>
///   <item>Allocate happy path + matriz fiscal + multi-moneda.</item>
///   <item>Void + reassociate.</item>
///   <item>Reconciliacion del denormalizado bc.ReceivedRefundAmount (DR2).</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class OperatorRefundServiceTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public OperatorRefundServiceTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // RecordReceivedAsync
    // =========================================================================

    [Fact]
    public async Task RecordReceived_realDb_creaRefundYManualCashMovementLinked()
    {
        // Setup: solo necesitamos un Supplier para el ingreso.
        var (supplierId, supplierPublicId) = await SeedSupplierAsync();

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var request = new RecordOperatorRefundRequest(
            SupplierPublicId: supplierPublicId,
            ReceivedAmount: 1500m,
            Currency: "ARS",
            ReceivedAt: DateTime.UtcNow,
            Method: "Transfer",
            Reference: "Op-12345",
            Notes: "Pago consolidado de 3 reservas");

        var dto = await svc.RecordReceivedAsync(request, "test-user", "Test User", CancellationToken.None);

        Assert.NotEqual(Guid.Empty, dto.PublicId);
        Assert.Equal(1500m, dto.ReceivedAmount);
        Assert.Equal(0m, dto.AllocatedAmount);
        Assert.Equal(1500m, dto.RemainingCap);

        await using var ctx = _fixture.CreateDbContext();
        var refund = await ctx.OperatorRefundReceived.AsNoTracking()
            .FirstAsync(r => r.PublicId == dto.PublicId);

        // TG4 (INV-CONT-09): el ManualCashMovement debe quedar linkeado via FK
        // OperatorRefundReceivedId, sin RelatedReservaId (N:M).
        var movement = await ctx.ManualCashMovements.AsNoTracking()
            .FirstOrDefaultAsync(m => m.OperatorRefundReceivedId == refund.Id);
        Assert.NotNull(movement);
        Assert.Equal(CashMovementDirections.Income, movement.Direction);
        Assert.Equal(1500m, movement.Amount);
        Assert.Equal("OperatorRefund", movement.Category);
        Assert.Null(movement.RelatedReservaId);
        Assert.Equal(supplierId, movement.RelatedSupplierId);
        Assert.Contains("Pago consolidado", movement.Description);
    }

    [Fact]
    public async Task RecordReceived_montoCero_Rechaza()
    {
        var (_, supplierPublicId) = await SeedSupplierAsync();
        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RecordReceivedAsync(
                new RecordOperatorRefundRequest(supplierPublicId, 0m, "ARS", DateTime.UtcNow, null, null, null),
                "user", null, CancellationToken.None));
    }

    // =========================================================================
    // AllocateAsync — happy path + matriz fiscal
    // =========================================================================

    [Fact]
    public async Task Allocate_realDb_creaAllocationYClientCreditEntry()
    {
        var seed = await SeedFullScenarioAsync(receivedAmount: 1000m, agencyCondition: "RESPONSABLE_INSCRIPTO");

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var dto = await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.BcPublicId, 500m, new List<DeductionLineRequest>
            {
                new(Kind: DeductionKind.AdministrativeFee, Amount: 50m, Description: "Cargo administrativo",
                    CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null,
                    Jurisdiction: null, ForeignCountryCode: null,
                    SupportingDocumentRef: "REF-123", JustificationComment: null,
                    MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false),
            }),
            "test-user", "Test User", CancellationToken.None);

        Assert.Equal(500m, dto.GrossAmount);
        Assert.Equal(450m, dto.NetAmount); // 500 - 50
        Assert.False(dto.IsVoided);
        Assert.Single(dto.Deductions);

        // Verificar ClientCreditEntry creado con balance = NetAmount.
        await using var ctx = _fixture.CreateDbContext();
        var entry = await ctx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(c => c.BookingCancellationId == seed.BcId);
        Assert.Equal(450m, entry.CreditedAmount);
        Assert.Equal(450m, entry.RemainingBalance);
        Assert.False(entry.IsFullyConsumed);
    }

    [Fact]
    public async Task Allocate_realDb_actualizaBcStatusAClientCreditApplied()
    {
        var seed = await SeedFullScenarioAsync(receivedAmount: 1000m, agencyCondition: "RESPONSABLE_INSCRIPTO");

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.BcPublicId, 500m, new List<DeductionLineRequest>()),
            "test-user", null, CancellationToken.None);

        await using var ctx = _fixture.CreateDbContext();
        var bc = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == seed.BcPublicId);
        Assert.Equal(BookingCancellationStatus.ClientCreditApplied, bc.Status);
        Assert.Equal(500m, bc.ReceivedRefundAmount);
    }

    [Fact]
    public async Task Allocate_conMatrixFiscalRIOperatorMono_rechazaIvaWithholding_INV105()
    {
        // Supplier Monotributo, Agency RI. IVA withholding sobre Mono = INV-105.
        var seed = await SeedFullScenarioAsync(
            receivedAmount: 1000m,
            agencyCondition: "RESPONSABLE_INSCRIPTO",
            supplierCondition: "MONOTRIBUTISTA");

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.AllocateAsync(
                seed.RefundPublicId,
                new AllocateRefundRequest(seed.BcPublicId, 500m, new List<DeductionLineRequest>
                {
                    new(Kind: DeductionKind.IvaWithholding, Amount: 50m, Description: null,
                        CertificateNumber: "CERT-1", CertificateDate: DateTime.UtcNow,
                        CertificatePdfUrl: null, Jurisdiction: null, ForeignCountryCode: null,
                        SupportingDocumentRef: null, JustificationComment: null,
                        MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false),
                }),
                "test-user", null, CancellationToken.None));

        Assert.Equal("INV-105", ex.InvariantCode);
    }

    [Fact]
    public async Task Allocate_conMatrixFiscalMonoOperatorRI_rechazaIvaWithholding_INV115()
    {
        // Agency Monotributo, Supplier RI. Retencion AR sin credito fiscal = INV-115.
        var seed = await SeedFullScenarioAsync(
            receivedAmount: 1000m,
            agencyCondition: "MONOTRIBUTISTA",
            supplierCondition: "RESPONSABLE_INSCRIPTO");

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.AllocateAsync(
                seed.RefundPublicId,
                new AllocateRefundRequest(seed.BcPublicId, 500m, new List<DeductionLineRequest>
                {
                    new(Kind: DeductionKind.IvaWithholding, Amount: 50m, Description: null,
                        CertificateNumber: "CERT-1", CertificateDate: DateTime.UtcNow,
                        CertificatePdfUrl: null, Jurisdiction: null, ForeignCountryCode: null,
                        SupportingDocumentRef: null, JustificationComment: null,
                        MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false),
                }),
                "test-user", null, CancellationToken.None));

        Assert.Equal("INV-115", ex.InvariantCode);
    }

    [Fact]
    public async Task Allocate_conDeductionForeignTax_OK()
    {
        // ForeignTax (kind 40) NO esta en el rango 10..39, asi que cualquier combo Mono/RI lo acepta.
        var seed = await SeedFullScenarioAsync(
            receivedAmount: 1000m,
            agencyCondition: "MONOTRIBUTISTA",
            supplierCondition: "EXTRANJERO");

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var dto = await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.BcPublicId, 500m, new List<DeductionLineRequest>
            {
                new(Kind: DeductionKind.ForeignTax, Amount: 30m, Description: "Brasil ITF",
                    CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null,
                    Jurisdiction: null, ForeignCountryCode: "BR",
                    SupportingDocumentRef: null, JustificationComment: null,
                    MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false),
            }),
            "test-user", null, CancellationToken.None);

        Assert.Equal(470m, dto.NetAmount);
    }

    [Fact]
    public async Task Allocate_conMonedaDistinta_RechazaInvariant()
    {
        // Refund en USD, BC con FiscalSnapshot en ARS. Debe rechazar INV-118.
        var seed = await SeedFullScenarioAsync(
            receivedAmount: 1000m,
            agencyCondition: "RESPONSABLE_INSCRIPTO",
            refundCurrency: "USD",
            bcCurrency: "ARS");

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.AllocateAsync(
                seed.RefundPublicId,
                new AllocateRefundRequest(seed.BcPublicId, 500m, new List<DeductionLineRequest>()),
                "test-user", null, CancellationToken.None));

        Assert.Equal("INV-118", ex.InvariantCode);
    }

    [Fact]
    public async Task ReceivedRefundAmount_MatchesSumOfAllocations()
    {
        // DR2 plan v3: el denormalizado bc.ReceivedRefundAmount debe coincidir
        // con SUM(allocations.NetAmount activas) del BC.
        var seed = await SeedFullScenarioAsync(receivedAmount: 1000m, agencyCondition: "RESPONSABLE_INSCRIPTO");

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.BcPublicId, 400m, new List<DeductionLineRequest>()),
            "test-user", null, CancellationToken.None);

        await using var ctx = _fixture.CreateDbContext();
        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == seed.BcPublicId);
        var sumNet = await ctx.OperatorRefundAllocations
            .Where(a => a.BookingCancellationId == seed.BcId && !a.IsVoided)
            .SumAsync(a => a.NetAmount);

        Assert.Equal(sumNet, bc.ReceivedRefundAmount);
    }

    // =========================================================================
    // VoidAllocation
    // =========================================================================

    [Fact]
    public async Task Void_realDb_liberaCap_permitReallocation()
    {
        var seed = await SeedFullScenarioAsync(receivedAmount: 500m, agencyCondition: "RESPONSABLE_INSCRIPTO");

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var alloc = await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.BcPublicId, 500m, new List<DeductionLineRequest>()),
            "user", null, CancellationToken.None);

        // Void.
        var voided = await svc.VoidAllocationAsync(
            alloc.PublicId,
            new VoidAllocationRequest("Cashier se equivoco, anulo allocation completa"),
            "user", null, CancellationToken.None);

        Assert.True(voided.IsVoided);
        Assert.NotNull(voided.VoidedAt);

        await using var ctx = _fixture.CreateDbContext();
        var refund = await ctx.OperatorRefundReceived.AsNoTracking().FirstAsync(r => r.PublicId == seed.RefundPublicId);
        Assert.Equal(0m, refund.AllocatedAmount);

        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == seed.BcPublicId);
        Assert.Equal(0m, bc.ReceivedRefundAmount);
        // Volvio al estado previo a la primera allocation.
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);
    }

    [Fact]
    public async Task Void_DoubleVoid_Rechaza()
    {
        var seed = await SeedFullScenarioAsync(receivedAmount: 500m, agencyCondition: "RESPONSABLE_INSCRIPTO");

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var alloc = await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.BcPublicId, 500m, new List<DeductionLineRequest>()),
            "user", null, CancellationToken.None);

        await svc.VoidAllocationAsync(
            alloc.PublicId,
            new VoidAllocationRequest("Primer void valido para anular allocation"),
            "user", null, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.VoidAllocationAsync(
                alloc.PublicId,
                new VoidAllocationRequest("Intento de doble void no permitido en la BD"),
                "user", null, CancellationToken.None));
        Assert.Equal("INV-093", ex.InvariantCode);
    }

    // =========================================================================
    // ReassociateAllocation
    // =========================================================================

    [Fact]
    public async Task Reassociate_realDb_muevaAllocationEntreBCs()
    {
        // 2 BCs en estado AwaitingOperatorRefund. Allocate sobre BC1, reassociate a BC2.
        var seed = await SeedTwoBcScenarioAsync(receivedAmount: 1000m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var alloc = await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.Bc1PublicId, 500m, new List<DeductionLineRequest>()),
            "user", null, CancellationToken.None);

        var newAlloc = await svc.ReassociateAllocationAsync(
            alloc.PublicId,
            new ReassociateAllocationRequest(seed.Bc2PublicId, "El cashier asocio mal el ingreso a la reserva incorrecta"),
            "user", null, CancellationToken.None);

        Assert.NotEqual(alloc.PublicId, newAlloc.PublicId);
        Assert.Equal(seed.Bc2PublicId, newAlloc.BookingCancellationPublicId);
        Assert.False(newAlloc.IsVoided);

        await using var ctx = _fixture.CreateDbContext();
        // BC1 volvio a AwaitingOperatorRefund (sin allocations activas).
        var bc1 = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == seed.Bc1PublicId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc1.Status);
        Assert.Equal(0m, bc1.ReceivedRefundAmount);

        // BC2 paso a ClientCreditApplied.
        var bc2 = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == seed.Bc2PublicId);
        Assert.Equal(BookingCancellationStatus.ClientCreditApplied, bc2.Status);
        Assert.Equal(500m, bc2.ReceivedRefundAmount);

        // Cap del refund sigue en $500 (la antigua se libero + la nueva se sumo).
        var refund = await ctx.OperatorRefundReceived.AsNoTracking().FirstAsync(r => r.PublicId == seed.RefundPublicId);
        Assert.Equal(500m, refund.AllocatedAmount);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<(int SupplierId, Guid SupplierPublicId)> SeedSupplierAsync()
    {
        await using var ctx = _fixture.CreateDbContext();
        var supplier = new Supplier { Name = "Op Test", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        return (supplier.Id, supplier.PublicId);
    }

    private record FullScenarioResult(
        Guid RefundPublicId,
        Guid BcPublicId,
        int BcId);

    /// <summary>
    /// Customer + Supplier + Reserva + Invoice + 1 BC en AwaitingOperatorRefund
    /// + 1 OperatorRefundReceived listo para allocate.
    /// </summary>
    private async Task<FullScenarioResult> SeedFullScenarioAsync(
        decimal receivedAmount,
        string agencyCondition = "RESPONSABLE_INSCRIPTO",
        string supplierCondition = "RESPONSABLE_INSCRIPTO",
        string refundCurrency = "ARS",
        string bcCurrency = "ARS")
    {
        await using var ctx = _fixture.CreateDbContext();

        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        // lineCurrency = bcCurrency: la moneda de la LINEA del operador (donde vive INV-118)
        // sigue a la moneda del evento. El test de INV-118 manda refundCurrency=USD vs bcCurrency=ARS,
        // asi la linea queda en ARS y el refund USD rebota correcto en INV-118 (no en INV-126).
        var bc = CancellationTestData.NewCancellation(
            custId, supId, resId, invId, BookingCancellationStatus.AwaitingOperatorRefund,
            lineCurrency: bcCurrency);
        bc.FiscalSnapshot.AgencyTaxConditionAtEvent = agencyCondition;
        bc.FiscalSnapshot.SupplierTaxConditionAtEvent = supplierCondition;
        bc.FiscalSnapshot.CurrencyAtEvent = bcCurrency;
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supId,
            ReceivedAmount = receivedAmount,
            AllocatedAmount = 0m,
            Method = "Transfer",
            Currency = refundCurrency,
            ExchangeRateAtReceipt = 1m,
            ReceivedAt = DateTime.UtcNow,
            ReceivedByUserId = "seed",
            ReceivedByUserName = "Seed",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        return new FullScenarioResult(refund.PublicId, bc.PublicId, bc.Id);
    }

    private record TwoBcScenarioResult(
        Guid RefundPublicId,
        Guid Bc1PublicId,
        Guid Bc2PublicId);

    private async Task<TwoBcScenarioResult> SeedTwoBcScenarioAsync(decimal receivedAmount)
    {
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer { FullName = "C", TaxCondition = "Consumidor Final", IsActive = true };
        var supplier = new Supplier { Name = "S", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var r1 = new Reserva { NumeroReserva = "R1", Name = "R1", Status = EstadoReserva.PendingOperatorRefund, PayerId = customer.Id };
        var r2 = new Reserva { NumeroReserva = "R2", Name = "R2", Status = EstadoReserva.PendingOperatorRefund, PayerId = customer.Id };
        ctx.Reservas.AddRange(r1, r2);
        await ctx.SaveChangesAsync();

        var i1 = new Invoice { TipoComprobante = 1, PuntoDeVenta = 1, NumeroComprobante = 1, ImporteTotal = 1000m, ReservaId = r1.Id };
        var i2 = new Invoice { TipoComprobante = 1, PuntoDeVenta = 1, NumeroComprobante = 2, ImporteTotal = 1000m, ReservaId = r2.Id };
        ctx.Invoices.AddRange(i1, i2);
        await ctx.SaveChangesAsync();

        var bc1 = CancellationTestData.NewCancellation(customer.Id, supplier.Id, r1.Id, i1.Id, BookingCancellationStatus.AwaitingOperatorRefund);
        var bc2 = CancellationTestData.NewCancellation(customer.Id, supplier.Id, r2.Id, i2.Id, BookingCancellationStatus.AwaitingOperatorRefund);
        bc1.FiscalSnapshot.AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        bc2.FiscalSnapshot.AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        ctx.BookingCancellations.AddRange(bc1, bc2);
        await ctx.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id,
            ReceivedAmount = receivedAmount,
            AllocatedAmount = 0m,
            Method = "Transfer",
            Currency = "ARS",
            ExchangeRateAtReceipt = 1m,
            ReceivedAt = DateTime.UtcNow,
            ReceivedByUserId = "seed",
            ReceivedByUserName = "Seed",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        return new TwoBcScenarioResult(refund.PublicId, bc1.PublicId, bc2.PublicId);
    }
}
