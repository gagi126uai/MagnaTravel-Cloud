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
/// FC1.2.2 reviewer pass (2026-05-18): tests que cubren los bugs detectados en
/// el review de FC1.2.2 commits 88af5f4. Cada test apunta a un bug concreto:
/// <list type="bullet">
///   <item>T1 — F1: refund.AllocatedAmount debe usar NetAmount, no GrossAmount.</item>
///   <item>T3 — cross-check de F1: bc.ReceivedRefundAmount tambien suma netos.</item>
///   <item>T4 — F2: VoidAllocation libera SOLO el NetAmount del cap.</item>
///   <item>T5 — F3+F4: Reassociate no duplica cap (suma net = libera net).</item>
///   <item>T8 — F10: refund.SupplierId vs bc.SupplierId mismatch dispara INV-126.</item>
/// </list>
///
/// <para>
/// Patron: usa <see cref="PostgresIntegrationFixture"/> para validar contra
/// Postgres real (los CHECK SQL del modulo y el xmin concurrency token solo
/// existen en una BD real, no en InMemory).
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class OperatorRefundServiceFixesTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public OperatorRefundServiceFixesTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // T1 — refund.AllocatedAmount usa NetAmount (no GrossAmount).
    // =========================================================================

    /// <summary>
    /// Bug F1: si el service incrementa el cap del refund con GrossAmount, las
    /// deducciones consumen capacidad de la agencia que NO recibio (el operador
    /// se quedo esa plata). Test: refund de 1000, allocation gross 1000 con
    /// deducciones 200 -> AllocatedAmount esperado 800 (no 1000).
    /// </summary>
    [Fact]
    public async Task RefundAllocatedAmount_ConDeducciones_UsaNetAmount()
    {
        var seed = await SeedFullScenarioAsync(receivedAmount: 1000m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var dto = await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.BcPublicId, GrossAmount: 1000m, new List<DeductionLineRequest>
            {
                new(Kind: DeductionKind.AdministrativeFee, Amount: 200m, Description: "Penalidad operador 20%",
                    CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null,
                    Jurisdiction: null, ForeignCountryCode: null,
                    SupportingDocumentRef: null, JustificationComment: null,
                    MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false),
            }),
            "user", null, CancellationToken.None);

        Assert.Equal(1000m, dto.GrossAmount);
        Assert.Equal(800m, dto.NetAmount);

        await using var ctx = _fixture.CreateDbContext();
        var refund = await ctx.OperatorRefundReceived.AsNoTracking()
            .FirstAsync(r => r.PublicId == seed.RefundPublicId);

        // CORE del test: el cap consumido es el neto, no el gross. Si esto
        // falla con 1000m, F1 esta revertido.
        Assert.Equal(800m, refund.AllocatedAmount);
        Assert.Equal(200m, refund.ReceivedAmount - refund.AllocatedAmount); // RemainingCap
    }

    // =========================================================================
    // T3 — bc.ReceivedRefundAmount = SUM(allocations.NetAmount activas).
    // =========================================================================

    /// <summary>
    /// Cross-check del F1: el denormalizado bc.ReceivedRefundAmount tambien
    /// suma netos (no gross). Y refund.AllocatedAmount debe coincidir con esa
    /// misma suma neta para que reportes contables crucen ambos lados.
    /// Crea 2 allocations con deducciones distintas sobre el mismo BC.
    /// </summary>
    [Fact]
    public async Task ReceivedRefundAmount_ConDeducciones_CoincideConSumNet()
    {
        var seed = await SeedFullScenarioAsync(receivedAmount: 2000m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        // Allocation 1: gross 600 - 100 deduccion = neto 500.
        await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.BcPublicId, GrossAmount: 600m, new List<DeductionLineRequest>
            {
                new(Kind: DeductionKind.AdministrativeFee, Amount: 100m, Description: "Cargo admin",
                    CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null,
                    Jurisdiction: null, ForeignCountryCode: null,
                    SupportingDocumentRef: null, JustificationComment: null,
                    MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false),
            }),
            "user", null, CancellationToken.None);

        // Allocation 2: gross 400 - 50 deduccion = neto 350. Otro BC distinto
        // (no se puede tener 2 active allocations contra el mismo BC del mismo
        // refund por el unique partial index — usamos un segundo BC).
        var bc2PublicId = await SeedSecondBcAsync(seed.SupplierId, seed.CustomerId, seed.ReservaId);

        // Importante: segundo BC distinto. Verificamos sumas separadas por BC.
        var alloc2 = await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(bc2PublicId, GrossAmount: 400m, new List<DeductionLineRequest>
            {
                new(Kind: DeductionKind.BankingCost, Amount: 50m, Description: "Comision banco",
                    CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null,
                    Jurisdiction: null, ForeignCountryCode: null,
                    SupportingDocumentRef: null, JustificationComment: null,
                    MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false),
            }),
            "user", null, CancellationToken.None);

        Assert.Equal(350m, alloc2.NetAmount);

        await using var ctx = _fixture.CreateDbContext();

        // Cap del refund: suma de netos = 500 + 350 = 850.
        var refund = await ctx.OperatorRefundReceived.AsNoTracking()
            .FirstAsync(r => r.PublicId == seed.RefundPublicId);
        Assert.Equal(850m, refund.AllocatedAmount);

        // Suma de netos por SQL crudo (lo que verian los reportes contables).
        var sumNet = await ctx.OperatorRefundAllocations
            .Where(a => a.OperatorRefundReceivedId == refund.Id && !a.IsVoided)
            .SumAsync(a => a.NetAmount);
        Assert.Equal(sumNet, refund.AllocatedAmount);

        // Por BC: cada bc.ReceivedRefundAmount = su neto correspondiente.
        var bc1 = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == seed.BcPublicId);
        Assert.Equal(500m, bc1.ReceivedRefundAmount);

        var bc2 = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == bc2PublicId);
        Assert.Equal(350m, bc2.ReceivedRefundAmount);
    }

    // =========================================================================
    // T4 — VoidAllocation libera SOLO el NetAmount (espejo de F1).
    // =========================================================================

    /// <summary>
    /// Bug F2: si el void libera GrossAmount, AllocatedAmount queda negativo
    /// cuando habia deducciones (rompiendo el CHECK SQL AllocatedAmount >= 0).
    /// Test: allocate gross 500 con 100 ded (neto 400), void -> AllocatedAmount
    /// vuelve a 0 (no -100).
    /// </summary>
    [Fact]
    public async Task VoidAllocation_ConDeducciones_LiberaSoloNetDelCap()
    {
        var seed = await SeedFullScenarioAsync(receivedAmount: 1000m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var alloc = await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.BcPublicId, GrossAmount: 500m, new List<DeductionLineRequest>
            {
                new(Kind: DeductionKind.AdministrativeFee, Amount: 100m, Description: "Penalidad",
                    CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null,
                    Jurisdiction: null, ForeignCountryCode: null,
                    SupportingDocumentRef: null, JustificationComment: null,
                    MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false),
            }),
            "user", null, CancellationToken.None);

        Assert.Equal(400m, alloc.NetAmount);

        // Pre-void: cap consumido = 400 (neto).
        await using (var ctxPre = _fixture.CreateDbContext())
        {
            var refundPre = await ctxPre.OperatorRefundReceived.AsNoTracking()
                .FirstAsync(r => r.PublicId == seed.RefundPublicId);
            Assert.Equal(400m, refundPre.AllocatedAmount);
        }

        await svc.VoidAllocationAsync(
            alloc.PublicId,
            new VoidAllocationRequest("Cashier asocio mal la deduccion, anula allocation completa"),
            "user", null, CancellationToken.None);

        // Post-void: cap debe volver a 0 (no -100). Si F2 esta revertido y se
        // libera GrossAmount=500, el CHECK SQL tiraria DbUpdateException antes
        // del retorno; el test va a romper de otra forma. Ambos modos detectan
        // el bug.
        await using var ctx = _fixture.CreateDbContext();
        var refund = await ctx.OperatorRefundReceived.AsNoTracking()
            .FirstAsync(r => r.PublicId == seed.RefundPublicId);
        Assert.Equal(0m, refund.AllocatedAmount);

        var bc = await ctx.BookingCancellations.AsNoTracking()
            .FirstAsync(b => b.PublicId == seed.BcPublicId);
        Assert.Equal(0m, bc.ReceivedRefundAmount);
    }

    // =========================================================================
    // T5 — Reassociate no duplica cap (espejo de F3+F4).
    // =========================================================================

    /// <summary>
    /// Bug F3+F4: si Reassociate libera la vieja con gross y suma la nueva con
    /// gross (o cualquier mezcla net/gross), el cap del refund queda con un
    /// valor distinto al neto inicial. Test: el neto antes del reassociate
    /// debe ser igual al neto despues.
    /// </summary>
    [Fact]
    public async Task Reassociate_ConDeducciones_NoDuplicaCap()
    {
        // 2 BCs activos contra el mismo refund.
        var seed = await SeedTwoBcScenarioAsync(receivedAmount: 1500m);

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        // Allocate sobre BC1: gross 800 - 200 ded = neto 600.
        var alloc = await svc.AllocateAsync(
            seed.RefundPublicId,
            new AllocateRefundRequest(seed.Bc1PublicId, GrossAmount: 800m, new List<DeductionLineRequest>
            {
                new(Kind: DeductionKind.AdministrativeFee, Amount: 200m, Description: "Penalidad 25%",
                    CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null,
                    Jurisdiction: null, ForeignCountryCode: null,
                    SupportingDocumentRef: null, JustificationComment: null,
                    MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false),
            }),
            "user", null, CancellationToken.None);

        Assert.Equal(600m, alloc.NetAmount);

        decimal capAntes;
        await using (var ctxPre = _fixture.CreateDbContext())
        {
            var refundPre = await ctxPre.OperatorRefundReceived.AsNoTracking()
                .FirstAsync(r => r.PublicId == seed.RefundPublicId);
            capAntes = refundPre.AllocatedAmount;
            Assert.Equal(600m, capAntes);
        }

        // Reassociate a BC2.
        await svc.ReassociateAllocationAsync(
            alloc.PublicId,
            new ReassociateAllocationRequest(seed.Bc2PublicId, "El cashier asocio la allocation al BC incorrecto"),
            "user", null, CancellationToken.None);

        // Cap NETO del refund debe seguir en 600 (libero 600, sumo 600).
        await using var ctx = _fixture.CreateDbContext();
        var refund = await ctx.OperatorRefundReceived.AsNoTracking()
            .FirstAsync(r => r.PublicId == seed.RefundPublicId);
        Assert.Equal(capAntes, refund.AllocatedAmount);
        Assert.Equal(600m, refund.AllocatedAmount);

        // BC1 quedo sin allocations activas.
        var bc1 = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == seed.Bc1PublicId);
        Assert.Equal(0m, bc1.ReceivedRefundAmount);

        // BC2 recibio los 600 netos.
        var bc2 = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == seed.Bc2PublicId);
        Assert.Equal(600m, bc2.ReceivedRefundAmount);
    }

    // =========================================================================
    // T8 — INV-126: refund.SupplierId != bc.SupplierId rechaza.
    // =========================================================================

    /// <summary>
    /// Bug F10: hoy se podia allocate un refund de Supplier A contra un BC del
    /// Supplier B, ensuciando reportes contables por operador. INV-126 lo
    /// rechaza explicitamente antes de tocar la BD.
    /// </summary>
    [Fact]
    public async Task Allocate_RefundSupplierNoMatcheaBcSupplier_TiraInvariante()
    {
        // Setup manual con DOS suppliers distintos (los helpers existentes
        // siempre reutilizan el mismo Supplier para refund y BC).
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer { FullName = "Cliente", TaxCondition = "Consumidor Final", IsActive = true };
        var supplierA = new Supplier { Name = "Operador A", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.AddRange(supplierA, supplierB);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-MIX",
            Name = "Reserva mixta",
            Status = EstadoReserva.PendingOperatorRefund,
            PayerId = customer.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 1, PuntoDeVenta = 1, NumeroComprobante = 1,
            ImporteTotal = 1000m, ReservaId = reserva.Id, CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        // BC con SupplierId = B.
        var bc = CancellationTestData.NewCancellation(
            customer.Id, supplierB.Id, reserva.Id, invoice.Id,
            BookingCancellationStatus.AwaitingOperatorRefund);
        ctx.BookingCancellations.Add(bc);

        // Refund con SupplierId = A (no matchea).
        var refund = new OperatorRefundReceived
        {
            SupplierId = supplierA.Id,
            ReceivedAmount = 1000m,
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

        var refundPublicId = refund.PublicId;
        var bcPublicId = bc.PublicId;

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            svc.AllocateAsync(
                refundPublicId,
                new AllocateRefundRequest(bcPublicId, 500m, new List<DeductionLineRequest>()),
                "user", null, CancellationToken.None));

        Assert.Equal("INV-126", ex.InvariantCode);
        Assert.Contains("proveedor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Helpers (locales — copia minima del patron de OperatorRefundServiceTests).
    // =========================================================================

    private record FullScenarioResult(
        Guid RefundPublicId,
        Guid BcPublicId,
        int BcId,
        int SupplierId,
        int CustomerId,
        int ReservaId);

    private async Task<FullScenarioResult> SeedFullScenarioAsync(decimal receivedAmount)
    {
        await using var ctx = _fixture.CreateDbContext();

        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId, BookingCancellationStatus.AwaitingOperatorRefund);
        bc.FiscalSnapshot.AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        bc.FiscalSnapshot.SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        bc.FiscalSnapshot.CurrencyAtEvent = "ARS";
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supId,
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

        return new FullScenarioResult(refund.PublicId, bc.PublicId, bc.Id, supId, custId, resId);
    }

    /// <summary>
    /// Crea un segundo BC sobre OTRA reserva del mismo supplier+customer, para
    /// poder hacer 2 allocations activas contra el mismo refund (el unique
    /// partial index prohibe 2 allocations activas mismo refund+mismo BC).
    /// </summary>
    private async Task<Guid> SeedSecondBcAsync(int supplierId, int customerId, int existingReservaId)
    {
        await using var ctx = _fixture.CreateDbContext();
        // Reusa el existingReservaId solo para tener variedad: en este test
        // necesitamos otra reserva con otra invoice para crear el BC2.
        var reserva2 = new Reserva
        {
            NumeroReserva = "R-T3-2",
            Name = "Reserva 2",
            Status = EstadoReserva.PendingOperatorRefund,
            PayerId = customerId,
        };
        ctx.Reservas.Add(reserva2);
        await ctx.SaveChangesAsync();

        var invoice2 = new Invoice
        {
            TipoComprobante = 1, PuntoDeVenta = 1, NumeroComprobante = 99,
            ImporteTotal = 1000m, ReservaId = reserva2.Id, CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice2);
        await ctx.SaveChangesAsync();

        var bc2 = CancellationTestData.NewCancellation(customerId, supplierId, reserva2.Id, invoice2.Id, BookingCancellationStatus.AwaitingOperatorRefund);
        bc2.FiscalSnapshot.AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        bc2.FiscalSnapshot.SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO";
        bc2.FiscalSnapshot.CurrencyAtEvent = "ARS";
        ctx.BookingCancellations.Add(bc2);
        await ctx.SaveChangesAsync();

        return bc2.PublicId;
    }

    private record TwoBcScenarioResult(
        Guid RefundPublicId,
        Guid Bc1PublicId,
        Guid Bc2PublicId);

    private async Task<TwoBcScenarioResult> SeedTwoBcScenarioAsync(decimal receivedAmount)
    {
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer { FullName = "C-T5", TaxCondition = "Consumidor Final", IsActive = true };
        var supplier = new Supplier { Name = "S-T5", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var r1 = new Reserva { NumeroReserva = "R1-T5", Name = "R1", Status = EstadoReserva.PendingOperatorRefund, PayerId = customer.Id };
        var r2 = new Reserva { NumeroReserva = "R2-T5", Name = "R2", Status = EstadoReserva.PendingOperatorRefund, PayerId = customer.Id };
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
