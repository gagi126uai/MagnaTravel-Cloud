using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3 Fase 2 — F2.3 cascade tests (plan tactico Fase 2 §FC1.3.F2.3 punto 5, 2026-05-28,
/// cierra RH-005 + G-F2-D).
///
/// <para>Cubre el comportamiento nuevo de
/// <see cref="AfipService.ApplyCreditNoteEconomicReversalAsync"/>: distingue NC total vs
/// NC parcial usando el <c>BookingCancellation.CreditNoteKind</c> como discriminador primario
/// (fallback a comparacion por monto para NCs pre-FC1.3).</para>
///
/// <para><b>Tests</b>:
/// <list type="bullet">
///   <item><c>NcTotal_StillCascadesReceipt</c>: regresion FC1.2 — comportamiento intacto.</item>
///   <item><c>NcParcial_SinglePayment_NoCascade</c>: receipt original sigue Issued.</item>
///   <item><c>NcParcial_MultiPayments_NoCascade</c>: G-F2-D, 3 receipts sin cascade + audit.</item>
/// </list>
/// </para>
/// </summary>
public class AfipServicePartialCreditNoteReversalTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public AfipServicePartialCreditNoteReversalTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    private static AfipService BuildAfipService(AppDbContext context, IAuditService? auditService = null)
        => new(
            context,
            NullLogger<AfipService>.Instance,
            new HttpClient(),
            new NoopSensitiveDataProtector(),
            auditService);

    /// <summary>
    /// Helper: setea una factura original + NC con el kind y monto que pida el test.
    /// </summary>
    private static async Task SeedNcScenarioAsync(
        AppDbContext context,
        decimal originalAmount,
        decimal ncAmount,
        CreditNoteKind? bcCreditNoteKind, // null = no hay BC asociado (NCs pre-FC1.3)
        (decimal amount, bool receiptIssued)[]? payments = null)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-F23-001",
            Name = "Reserva F2.3",
            Status = EstadoReserva.Confirmed,
            TotalSale = originalAmount,
            TotalCost = 0m,
            Balance = 0m,
            TotalPaid = originalAmount
        };
        context.Reservas.Add(reserva);

        var original = new Invoice
        {
            Id = 800,
            ReservaId = 1,
            TipoComprobante = 6,
            PuntoDeVenta = 5,
            NumeroComprobante = 1234,
            Resultado = "A",
            ImporteTotal = originalAmount,
            ImporteNeto = originalAmount,
            ImporteIva = 0m,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            AnnulledByUserId = "user-123",
            AnnulledByUserName = "Backoffice",
        };
        context.Invoices.Add(original);

        var nc = new Invoice
        {
            Id = 801,
            ReservaId = 1,
            TipoComprobante = 8,
            PuntoDeVenta = 5,
            NumeroComprobante = 9999,
            Resultado = "A",
            ImporteTotal = ncAmount,
            ImporteNeto = ncAmount,
            ImporteIva = 0m,
            CreatedAt = DateTime.UtcNow,
            OriginalInvoiceId = 800,
        };
        context.Invoices.Add(nc);

        // Payments + Receipts vivos (Issued).
        if (payments != null)
        {
            int pId = 500;
            int rId = 700;
            foreach (var (amount, receiptIssued) in payments)
            {
                context.Payments.Add(new Payment
                {
                    Id = pId,
                    ReservaId = 1,
                    Amount = amount,
                    PaidAt = DateTime.UtcNow.AddDays(-2),
                    Method = "Transfer",
                    Status = "Paid",
                    EntryType = PaymentEntryTypes.Payment,
                    AffectsCash = true,
                    RelatedInvoiceId = 800, // FK a la factura original
                });
                if (receiptIssued)
                {
                    context.PaymentReceipts.Add(new PaymentReceipt
                    {
                        Id = rId,
                        PaymentId = pId,
                        ReservaId = 1,
                        ReceiptNumber = $"REC-{rId}",
                        Amount = amount,
                        Status = PaymentReceiptStatuses.Issued,
                        IssuedAt = DateTime.UtcNow.AddDays(-2),
                    });
                }
                pId++;
                rId++;
            }
        }

        // Si el caller especifica un kind => existe BookingCancellation asociado.
        if (bcCreditNoteKind.HasValue)
        {
            var supplier = new Supplier
            {
                Id = 1, Name = "Op", IsActive = true,
                TaxCondition = "IVA_RESP_INSCRIPTO", InvoicingMode = SupplierInvoicingMode.TotalToCustomer,
            };
            var customer = new Customer
            {
                Id = 1, FullName = "Cli", TaxCondition = "Consumidor Final", IsActive = true,
            };
            context.Suppliers.Add(supplier);
            context.Customers.Add(customer);

            context.BookingCancellations.Add(new BookingCancellation
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                ReservaId = 1,
                CustomerId = 1,
                SupplierId = 1,
                OriginatingInvoiceId = 800,
                CreditNoteInvoiceId = 801, // FK a la NC
                Status = BookingCancellationStatus.AwaitingFiscalConfirmation,
                Reason = "Cancelacion test",
                DraftedAt = DateTime.UtcNow,
                DraftedByUserId = "vendedor-1",
                CreditNoteKind = bcCreditNoteKind.Value,
                FiscalSnapshot = new FiscalSnapshot
                {
                    Source = ExchangeRateSource.BCRA_A3500,
                    ExchangeRateAtOriginalInvoice = 1m,
                    CurrencyAtEvent = "ARS",
                    FetchedAt = DateTime.UtcNow,
                },
            });
        }

        await context.SaveChangesAsync();
    }

    // =========================================================================
    // Test 1 (regression FC1.2): NC total con cascade sigue funcionando.
    // =========================================================================

    [Fact]
    public async Task ApplyCreditNoteEconomicReversal_NcTotal_StillCascadesReceipt()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        // NC TOTAL: ImporteTotal NC == ImporteTotal original. 1 payment + 1 receipt.
        await SeedNcScenarioAsync(
            ctx,
            originalAmount: 1000m,
            ncAmount: 1000m,
            bcCreditNoteKind: null, // NC sin BC asociado (fallback histroico aplica)
            payments: new[] { (1000m, true) });

        var auditMock = new Mock<IAuditService>();
        var service = BuildAfipService(ctx, auditMock.Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        // El receipt original quedo Voided (cascade FC1.2 sigue funcionando).
        var receipt = await ctx.PaymentReceipts.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync();
        Assert.Equal(PaymentReceiptStatuses.Voided, receipt.Status);

        // Audit "ReceiptVoidedByCascade" emitido.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            "ReceiptVoidedByCascade",
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // El Payment reversal apunta al payment original (no es null).
        var reversal = await ctx.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal);
        Assert.NotNull(reversal!.OriginalPaymentId);
        Assert.Equal(-1000m, reversal.Amount);
    }

    // =========================================================================
    // Test 2 (RH-005): NC parcial con single payment NO cascade.
    // =========================================================================

    [Fact]
    public async Task ApplyCreditNoteEconomicReversal_NcParcial_SinglePayment_NoCascade()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        // NC PARCIAL: ImporteTotal NC ($250) < ImporteTotal original ($1000).
        // BC asociado con kind=PartialOnOriginal.
        await SeedNcScenarioAsync(
            ctx,
            originalAmount: 1000m,
            ncAmount: 250m,
            bcCreditNoteKind: CreditNoteKind.PartialOnOriginal,
            payments: new[] { (1000m, true) });

        var auditMock = new Mock<IAuditService>();
        var service = BuildAfipService(ctx, auditMock.Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        // El receipt sigue Issued (NO cascade-voided).
        var receipt = await ctx.PaymentReceipts.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync();
        Assert.Equal(PaymentReceiptStatuses.Issued, receipt.Status);
        Assert.Null(receipt.VoidedAt);

        // Audit "PartialCreditNoteEconomicReversalNoCascade" emitido (NO el de cascade).
        auditMock.Verify(a => a.LogBusinessEventAsync(
            "PartialCreditNoteEconomicReversalNoCascade",
            "Invoice", "801",
            It.Is<string?>(d => d!.Contains("liveReceiptIds")),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        auditMock.Verify(a => a.LogBusinessEventAsync(
            "ReceiptVoidedByCascade",
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Payment reversal por el monto parcial CON OriginalPaymentId = null.
        var reversal = await ctx.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal);
        Assert.Null(reversal!.OriginalPaymentId);
        Assert.Equal(-250m, reversal.Amount);
    }

    // =========================================================================
    // Test 3 (G-F2-D): NC parcial multi-payments NO cascade + audit con receipt IDs.
    // =========================================================================

    [Fact]
    public async Task ApplyCreditNoteEconomicReversal_NcParcial_MultiPayments_NoCascade()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        // NC PARCIAL: factura $1000 + 3 payments ($300 + $300 + $400) con 3 receipts vivos.
        // BC asociado con kind=PartialOnOriginal. NC parcial $250.
        await SeedNcScenarioAsync(
            ctx,
            originalAmount: 1000m,
            ncAmount: 250m,
            bcCreditNoteKind: CreditNoteKind.PartialOnOriginal,
            payments: new[]
            {
                (300m, true),
                (300m, true),
                (400m, true),
            });

        string? capturedAuditDetails = null;
        var auditMock = new Mock<IAuditService>();
        auditMock
            .Setup(a => a.LogBusinessEventAsync(
                "PartialCreditNoteEconomicReversalNoCascade",
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, string, string?, CancellationToken>(
                (a, e, id, d, u, un, c) => capturedAuditDetails = d)
            .Returns(Task.CompletedTask);

        var service = BuildAfipService(ctx, auditMock.Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        // Los 3 receipts originales siguen Issued (ninguno cascade-voided).
        var receipts = await ctx.PaymentReceipts.IgnoreQueryFilters()
            .AsNoTracking().ToListAsync();
        Assert.Equal(3, receipts.Count);
        Assert.All(receipts, r => Assert.Equal(PaymentReceiptStatuses.Issued, r.Status));

        // Payment reversal CON OriginalPaymentId = null.
        var reversal = await ctx.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal);
        Assert.Null(reversal!.OriginalPaymentId);
        Assert.Equal(-250m, reversal.Amount);

        // Audit del nuevo evento con los 3 receipt IDs serializados.
        Assert.NotNull(capturedAuditDetails);
        var auditDoc = JsonDocument.Parse(capturedAuditDetails!);
        Assert.Equal(3, auditDoc.RootElement.GetProperty("liveReceiptCount").GetInt32());
        var receiptIdsFromAudit = auditDoc.RootElement
            .GetProperty("liveReceiptIds")
            .EnumerateArray()
            .Select(e => e.GetInt32())
            .ToList();
        Assert.Equal(3, receiptIdsFromAudit.Count);
        Assert.Equal(-250m, auditDoc.RootElement.GetProperty("reversalAmount").GetDecimal());
    }

    // =========================================================================
    // FC1.3 Fase 3 (ADR-010, 2026-05-29): alta del caso de reconciliacion en el
    // mismo path del reversal parcial. El caso nace junto al Payment reversal
    // (B1: misma transaccion) y SOLO si hay recibos vivos.
    // =========================================================================

    [Fact]
    public async Task ApplyPartialReversal_WithLiveReceipts_CreatesReconciliationCaseWithSnapshot()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        // NC parcial con 3 recibos vivos (G-F2-D).
        await SeedNcScenarioAsync(
            ctx,
            originalAmount: 1000m,
            ncAmount: 250m,
            bcCreditNoteKind: CreditNoteKind.PartialOnOriginal,
            payments: new[] { (300m, true), (300m, true), (400m, true) });

        var service = BuildAfipService(ctx, new Mock<IAuditService>().Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var reconciliation = await ctx.PartialCreditNoteReconciliations
            .AsNoTracking()
            .Include(r => r.Receipts)
            .SingleOrDefaultAsync();

        Assert.NotNull(reconciliation);
        Assert.Equal(801, reconciliation!.CreditNoteInvoiceId);
        Assert.Equal(800, reconciliation.OriginalInvoiceId);
        Assert.Equal(PartialCreditNoteReconciliationStatus.Pending, reconciliation.Status);
        // Monto fiscal acreditado = ImporteTotal de la NC parcial (en positivo).
        Assert.Equal(250m, reconciliation.FiscalAmountCredited);
        // OpenedBy sale del AnnulledByUserId de la factura original.
        Assert.Equal("user-123", reconciliation.OpenedByUserId);
        // Snapshot: una hija por cada recibo vivo.
        Assert.Equal(3, reconciliation.Receipts.Count);
        Assert.All(reconciliation.Receipts, c => Assert.Equal(PaymentReceiptStatuses.Issued, c.StatusAtOpen));
    }

    [Fact]
    public async Task ApplyPartialReversal_NoLiveReceipts_DoesNotCreateCase()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        // NC parcial pero SIN recibos emitidos (un payment sin receipt Issued).
        await SeedNcScenarioAsync(
            ctx,
            originalAmount: 1000m,
            ncAmount: 250m,
            bcCreditNoteKind: CreditNoteKind.PartialOnOriginal,
            payments: new[] { (1000m, false) });

        var service = BuildAfipService(ctx, new Mock<IAuditService>().Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        // Sin recibos vivos no hay nada que acomodar -> no se crea caso.
        var anyCase = await ctx.PartialCreditNoteReconciliations.AsNoTracking().AnyAsync();
        Assert.False(anyCase);
    }

    [Fact]
    public async Task ApplyPartialReversal_RunTwice_DoesNotDuplicateCase()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        await SeedNcScenarioAsync(
            ctx,
            originalAmount: 1000m,
            ncAmount: 250m,
            bcCreditNoteKind: CreditNoteKind.PartialOnOriginal,
            payments: new[] { (300m, true), (400m, true) });

        var service = BuildAfipService(ctx, new Mock<IAuditService>().Object);

        // Primer pasada: crea el reversal + el caso.
        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);
        // Segunda pasada (simula reintento de Hangfire): el guard del wrapper detecta
        // que ya existe el reversal y hace return ANTES de tocar nada -> no duplica el
        // caso. (B2: el indice unico en CreditNoteInvoiceId es la red de defensa, pero
        // el guard primario ya lo evita.)
        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var caseCount = await ctx.PartialCreditNoteReconciliations.AsNoTracking().CountAsync();
        Assert.Equal(1, caseCount);
    }

    /// <summary>
    /// Stub minimal para satisfacer el ctor de AfipService.
    /// </summary>
    private sealed class NoopSensitiveDataProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }
}
