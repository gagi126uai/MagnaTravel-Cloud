using System;
using System.Net.Http;
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
/// B1.15 (2026-05-11): refuerzo del cascade NC AFIP -> Receipt Voided.
///
/// Cubre <see cref="AfipService.ApplyCreditNoteEconomicReversalAsync"/>:
///  - Receipt Issued -> Voided con todos los audit fields (VoidedBy*, VoidReason).
///  - Idempotencia: Receipt Voided no se re-toca.
///  - Sin Payment match: no se intenta tocar nada.
///  - User propagado desde Invoice original (Invoice.AnnulledByUserId/Name).
///  - Fallback "system"/"Sistema" cuando la invoice original no tiene user.
///  - Audit log emitido con accion <c>ReceiptVoidedByCascade</c> (distinto de
///    <c>ReceiptVoided</c> del void manual via PaymentService).
///  - Payment de reversion economica (CreditNoteReversal) siempre se crea.
///
/// Decision de diseno: el user del cascade se lee de la invoice original via
/// Include(OriginalInvoice), NO desde un parametro nuevo en ProcessInvoiceJob.
/// Esto evita riesgos Hangfire (serializacion de jobs encolados pre-deploy).
/// </summary>
public class AfipServiceCascadeReceiptVoidTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public AfipServiceCascadeReceiptVoidTests()
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
    /// Seed: una Reserva con un Payment de $200 + Receipt Issued, una Invoice
    /// original (Factura B aprobada por AFIP) anulada por el user dado, y una NC
    /// (TipoComprobante=8, Resultado="A") con OriginalInvoiceId hacia la original.
    /// </summary>
    private static async Task SeedAnnulmentScenarioAsync(
        AppDbContext context,
        string? annulledByUserId,
        string? annulledByUserName,
        decimal amount = 200m,
        bool issueReceipt = true,
        bool receiptAlreadyVoided = false)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0010",
            Name = "Reserva cascade test",
            Status = EstadoReserva.Confirmed,
            TotalSale = amount,
            TotalCost = 0m,
            Balance = 0m,
            TotalPaid = amount
        };
        context.Reservas.Add(reserva);

        var payment = new Payment
        {
            Id = 500,
            ReservaId = 1,
            Amount = amount,
            PaidAt = DateTime.UtcNow.AddDays(-2),
            Method = "Transfer",
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true
        };
        context.Payments.Add(payment);

        if (issueReceipt)
        {
            context.PaymentReceipts.Add(new PaymentReceipt
            {
                Id = 700,
                PaymentId = 500,
                ReservaId = 1,
                ReceiptNumber = "REC-100",
                Amount = amount,
                Status = receiptAlreadyVoided ? PaymentReceiptStatuses.Voided : PaymentReceiptStatuses.Issued,
                IssuedAt = DateTime.UtcNow.AddDays(-2),
                VoidedAt = receiptAlreadyVoided ? DateTime.UtcNow.AddDays(-1) : (DateTime?)null,
                VoidedByUserId = receiptAlreadyVoided ? "previous-void-user" : null,
                VoidedByUserName = receiptAlreadyVoided ? "Previous User" : null,
                VoidReason = receiptAlreadyVoided ? "Void previo manual" : null
            });
        }

        // Invoice original (Factura B aprobada) — anulada por annulledBy*.
        var original = new Invoice
        {
            Id = 800,
            ReservaId = 1,
            TipoComprobante = 6, // Factura B
            PuntoDeVenta = 5,
            NumeroComprobante = 1234,
            Resultado = "A",
            ImporteTotal = amount,
            ImporteNeto = amount,
            ImporteIva = 0m,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            AnnulledByUserId = annulledByUserId,
            AnnulledByUserName = annulledByUserName,
            AnnulmentStatus = AnnulmentStatus.Pending,
            AnnulmentReason = "Test cascade"
        };
        context.Invoices.Add(original);

        // NC (cbteTipo=8) aprobada — esta es la que dispara el cascade.
        var nc = new Invoice
        {
            Id = 801,
            ReservaId = 1,
            TipoComprobante = 8, // NC B
            PuntoDeVenta = 5,
            NumeroComprobante = 9999,
            Resultado = "A",
            ImporteTotal = amount,
            ImporteNeto = amount,
            ImporteIva = 0m,
            CreatedAt = DateTime.UtcNow,
            OriginalInvoiceId = 800
        };
        context.Invoices.Add(nc);

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Cascade_WhenReceiptIssued_VoidsReceipt_WithFullAuditTrail()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAnnulmentScenarioAsync(
            context, annulledByUserId: "user-123", annulledByUserName: "Carlos Backoffice");

        var auditMock = new Mock<IAuditService>();
        var service = BuildAfipService(context, auditMock.Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var receipt = await context.PaymentReceipts.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync(r => r.Id == 700);

        Assert.Equal(PaymentReceiptStatuses.Voided, receipt.Status);
        Assert.NotNull(receipt.VoidedAt);
        Assert.Equal("user-123", receipt.VoidedByUserId);
        Assert.Equal("Carlos Backoffice", receipt.VoidedByUserName);
        Assert.NotNull(receipt.VoidReason);
        Assert.Contains("NC AFIP", receipt.VoidReason!);
        Assert.Contains("00005-00009999", receipt.VoidReason!); // PuntoDeVenta + NumeroComprobante formato D5/D8
        Assert.Contains("Invoice #801", receipt.VoidReason!);
        // Numero original preservado.
        Assert.Equal("REC-100", receipt.ReceiptNumber);

        // Audit log emitido con accion diferenciada.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            "ReceiptVoidedByCascade",
            "PaymentReceipt",
            "700",
            It.Is<string>(d => d!.Contains("NC AFIP")),
            "user-123",
            "Carlos Backoffice",
            It.IsAny<CancellationToken>()), Times.Once);

        // Reversion economica creada.
        var reversal = await context.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal);
        Assert.Equal(-200m, reversal!.Amount);
        Assert.Equal(801, reversal.RelatedInvoiceId);
        Assert.False(reversal.AffectsCash);
    }

    [Fact]
    public async Task Cascade_WhenReceiptAlreadyVoided_DoesNotRetouch_AndDoesNotEmitAudit()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAnnulmentScenarioAsync(
            context, annulledByUserId: "user-123", annulledByUserName: "Carlos Backoffice",
            receiptAlreadyVoided: true);

        var auditMock = new Mock<IAuditService>();
        var service = BuildAfipService(context, auditMock.Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var receipt = await context.PaymentReceipts.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync(r => r.Id == 700);

        // Audit fields del void previo se preservan (NO se re-tocan).
        Assert.Equal(PaymentReceiptStatuses.Voided, receipt.Status);
        Assert.Equal("previous-void-user", receipt.VoidedByUserId);
        Assert.Equal("Previous User", receipt.VoidedByUserName);
        Assert.Equal("Void previo manual", receipt.VoidReason);

        // No audit log de cascade (idempotencia).
        auditMock.Verify(a => a.LogBusinessEventAsync(
            "ReceiptVoidedByCascade",
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Reversion economica SI se crea igual (es independiente del receipt).
        var reversal = await context.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal);
    }

    [Fact]
    public async Task Cascade_WhenNoMatchingPayment_DoesNotTouchAnyReceipt()
    {
        await using var context = new AppDbContext(_dbOptions);
        // Seed sin Payment ni Receipt (solo invoice original + NC).
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0011",
            Name = "Reserva sin payment",
            Status = EstadoReserva.Confirmed
        };
        context.Reservas.Add(reserva);
        context.Invoices.Add(new Invoice
        {
            Id = 800, ReservaId = 1, TipoComprobante = 6,
            PuntoDeVenta = 5, NumeroComprobante = 1234, Resultado = "A",
            ImporteTotal = 200m, ImporteNeto = 200m, ImporteIva = 0m,
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            AnnulledByUserId = "user-123", AnnulledByUserName = "Carlos Backoffice"
        });
        context.Invoices.Add(new Invoice
        {
            Id = 801, ReservaId = 1, TipoComprobante = 8,
            PuntoDeVenta = 5, NumeroComprobante = 9999, Resultado = "A",
            ImporteTotal = 200m, ImporteNeto = 200m, ImporteIva = 0m,
            CreatedAt = DateTime.UtcNow, OriginalInvoiceId = 800
        });
        await context.SaveChangesAsync();

        var auditMock = new Mock<IAuditService>();
        var service = BuildAfipService(context, auditMock.Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        // No hay receipts (ni habia antes).
        Assert.Equal(0, await context.PaymentReceipts.IgnoreQueryFilters().CountAsync());
        // No audit event.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            "ReceiptVoidedByCascade",
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // La reversion economica SIGUE creandose (su existencia no depende del Payment match).
        var reversal = await context.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal);
        Assert.Null(reversal!.OriginalPaymentId); // sin payment match
    }

    [Fact]
    public async Task Cascade_WhenOriginalHasNoAnnulledByUser_FallsBackToSystem()
    {
        await using var context = new AppDbContext(_dbOptions);
        // Original sin AnnulledByUserId/Name (paths que no llegan via EnqueueAnnulmentAsync).
        await SeedAnnulmentScenarioAsync(
            context, annulledByUserId: null, annulledByUserName: null);

        var auditMock = new Mock<IAuditService>();
        var service = BuildAfipService(context, auditMock.Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var receipt = await context.PaymentReceipts.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync(r => r.Id == 700);

        Assert.Equal(PaymentReceiptStatuses.Voided, receipt.Status);
        Assert.Equal("system", receipt.VoidedByUserId);
        Assert.Equal("Sistema", receipt.VoidedByUserName);

        auditMock.Verify(a => a.LogBusinessEventAsync(
            "ReceiptVoidedByCascade",
            "PaymentReceipt", "700",
            It.IsAny<string?>(),
            "system",
            "Sistema",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cascade_WhenAuditServiceNull_FallsBackToLogger_WithoutThrowing()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAnnulmentScenarioAsync(
            context, annulledByUserId: "user-123", annulledByUserName: "Carlos Backoffice");

        // _auditService = null (caso legacy / DI sin registrar audit).
        var service = BuildAfipService(context, auditService: null);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var receipt = await context.PaymentReceipts.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync(r => r.Id == 700);

        // El cascade siguio funcionando aunque audit estaba null.
        Assert.Equal(PaymentReceiptStatuses.Voided, receipt.Status);
        Assert.Equal("user-123", receipt.VoidedByUserId);
        Assert.NotNull(receipt.VoidReason);
    }

    [Fact]
    public async Task Cascade_IsIdempotent_RunningTwiceDoesNotDuplicateReversal()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAnnulmentScenarioAsync(
            context, annulledByUserId: "user-123", annulledByUserName: "Carlos Backoffice");

        var auditMock = new Mock<IAuditService>();
        var service = BuildAfipService(context, auditMock.Object);

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);
        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801); // segunda llamada

        var reversals = await context.Payments.AsNoTracking()
            .Where(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal)
            .ToListAsync();
        Assert.Single(reversals); // solo una reversion economica

        // Audit log emitido solo 1 vez (segunda llamada cortocircuita en
        // existingReversal != null antes de tocar el receipt).
        auditMock.Verify(a => a.LogBusinessEventAsync(
            "ReceiptVoidedByCascade",
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Stub minimal para satisfacer el ctor de AfipService en tests.
    /// El cascade no usa este servicio.
    /// </summary>
    private sealed class NoopSensitiveDataProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }
}
