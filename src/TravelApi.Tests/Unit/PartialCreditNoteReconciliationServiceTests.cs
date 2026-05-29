using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3 Fase 3 (ADR-010, 2026-05-29): tests unit del service de la bandeja de
/// reconciliacion de NC parciales con recibos vivos (listado + cierre manual).
///
/// <para>InMemory + Moq, sin Docker (DB es VPS remoto). Lo que NO se cubre aca: los
/// CHECK constraints SQL y la concurrencia xmin (InMemory no los soporta) — eso son
/// integration tests que corren en VPS. Lo que SI se cubre: el flujo del service
/// (idempotencia del cierre, 4-ojos, notas obligatorias con recibos vivos, lectura
/// en vivo del estado de recibos en el listado).</para>
/// </summary>
public class PartialCreditNoteReconciliationServiceTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fc13-fase3-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Construye el service con mocks. Por defecto el evaluator de 4-ojos devuelve false
    /// (bypass NO aplica) — los tests que necesitan bypass lo sobre-configuran.
    /// </summary>
    private static (
        PartialCreditNoteReconciliationService Service,
        AppDbContext Ctx,
        Mock<IFourEyesBypassEvaluator> BypassMock,
        Mock<IAuditService> AuditMock
    ) BuildService(bool bypassApplies = false)
    {
        var ctx = NewDbContext();

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { Allow4EyesBypassWhenSingleAdmin = bypassApplies });

        var bypassMock = new Mock<IFourEyesBypassEvaluator>();
        bypassMock.Setup(e => e.EvaluateAsync(It.IsAny<string?>(), It.IsAny<OperationalFinanceSettings>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bypassApplies);

        var auditMock = new Mock<IAuditService>();

        var service = new PartialCreditNoteReconciliationService(
            ctx,
            settingsMock.Object,
            bypassMock.Object,
            auditMock.Object,
            NullLogger<PartialCreditNoteReconciliationService>.Instance);

        return (service, ctx, bypassMock, auditMock);
    }

    /// <summary>
    /// Inserta un caso Pending con N recibos. Cada recibo se crea como PaymentReceipt
    /// real (con Payment) para que la lectura en vivo del estado funcione. Devuelve el
    /// PublicId del caso.
    /// </summary>
    private static async Task<Guid> SeedCaseAsync(
        AppDbContext ctx,
        string openedByUserId = "user-abrio",
        params (decimal amount, string status)[] receipts)
    {
        var original = new Invoice
        {
            Id = 800, ReservaId = null, TipoComprobante = 6, PuntoDeVenta = 5,
            NumeroComprobante = 1234, Resultado = "A", ImporteTotal = 1000m,
        };
        var nc = new Invoice
        {
            Id = 801, ReservaId = null, TipoComprobante = 8, PuntoDeVenta = 5,
            NumeroComprobante = 9999, Resultado = "A", ImporteTotal = 250m, OriginalInvoiceId = 800,
        };
        ctx.Invoices.AddRange(original, nc);

        var snapshotChildren = new List<PartialCreditNoteReconciliationReceipt>();
        int pId = 500;
        int rId = 700;
        foreach (var (amount, status) in receipts)
        {
            var payment = new Payment
            {
                Id = pId, ReservaId = null, Amount = amount, PaidAt = DateTime.UtcNow,
                Method = "Transfer", Status = "Paid", EntryType = PaymentEntryTypes.Payment,
                AffectsCash = true, RelatedInvoiceId = 800,
            };
            var receipt = new PaymentReceipt
            {
                Id = rId, PaymentId = pId, ReservaId = 0, ReceiptNumber = $"REC-{rId}",
                Amount = amount, Status = status, IssuedAt = DateTime.UtcNow,
            };
            ctx.Payments.Add(payment);
            ctx.PaymentReceipts.Add(receipt);

            snapshotChildren.Add(new PartialCreditNoteReconciliationReceipt
            {
                PaymentReceiptId = rId, PaymentId = pId, Amount = amount,
                StatusAtOpen = PaymentReceiptStatuses.Issued,
            });
            pId++;
            rId++;
        }

        var reconciliation = new PartialCreditNoteReconciliation
        {
            PublicId = Guid.NewGuid(),
            CreditNoteInvoiceId = 801,
            OriginalInvoiceId = 800,
            FiscalAmountCredited = 250m,
            Currency = "ARS",
            Status = PartialCreditNoteReconciliationStatus.Pending,
            OpenedAt = DateTime.UtcNow,
            OpenedByUserId = openedByUserId,
            OpenedByUserName = "Quien Abrio",
            Receipts = snapshotChildren,
        };
        ctx.PartialCreditNoteReconciliations.Add(reconciliation);

        await ctx.SaveChangesAsync();
        return reconciliation.PublicId;
    }

    // =========================================================================
    // Resolve: happy path (otra persona cierra, recibos ya anulados).
    // =========================================================================

    [Fact]
    public async Task Resolve_DifferentUser_AllReceiptsVoided_ClosesCaseWithoutNotes()
    {
        var (service, ctx, _, auditMock) = BuildService();
        var publicId = await SeedCaseAsync(ctx, openedByUserId: "user-abrio",
            receipts: new[] { (300m, PaymentReceiptStatuses.Voided), (400m, PaymentReceiptStatuses.Voided) });

        var result = await service.ResolveAsync(
            publicId,
            new ResolvePartialCreditNoteReconciliationRequest { Notes = null },
            currentUserId: "user-otro",
            currentUserName: "Otro Encargado",
            CancellationToken.None);

        Assert.Equal("Resolved", result.Status);
        Assert.False(result.ClosedWithLiveReceipts);
        Assert.False(result.FourEyesBypassApplied);
        Assert.Equal("Otro Encargado", result.ResolvedByUserName);

        var entity = await ctx.PartialCreditNoteReconciliations.AsNoTracking().SingleAsync();
        Assert.Equal(PartialCreditNoteReconciliationStatus.Resolved, entity.Status);
        Assert.NotNull(entity.ResolvedAt);

        auditMock.Verify(a => a.LogBusinessEventAsync(
            "PartialCreditNoteReconciliationResolved",
            "PartialCreditNoteReconciliation", It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================
    // Resolve: ya resuelto -> 409 (InvalidOperationException).
    // =========================================================================

    [Fact]
    public async Task Resolve_AlreadyResolved_Throws()
    {
        var (service, ctx, _, _) = BuildService();
        var publicId = await SeedCaseAsync(ctx, openedByUserId: "user-abrio",
            receipts: new[] { (300m, PaymentReceiptStatuses.Voided) });

        // Cerrar una vez.
        await service.ResolveAsync(publicId,
            new ResolvePartialCreditNoteReconciliationRequest(),
            "user-otro", "Otro", CancellationToken.None);

        // Segundo intento -> conflicto.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveAsync(publicId,
                new ResolvePartialCreditNoteReconciliationRequest(),
                "user-otro", "Otro", CancellationToken.None));
    }

    // =========================================================================
    // Resolve: self-close SIN bypass habilitado -> 409.
    // =========================================================================

    [Fact]
    public async Task Resolve_SelfClose_WithoutBypass_Throws()
    {
        var (service, ctx, _, _) = BuildService(bypassApplies: false);
        var publicId = await SeedCaseAsync(ctx, openedByUserId: "user-abrio",
            receipts: new[] { (300m, PaymentReceiptStatuses.Voided) });

        // El MISMO usuario que abrio intenta cerrar, sin bypass habilitado.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveAsync(publicId,
                new ResolvePartialCreditNoteReconciliationRequest { Notes = "intento" },
                currentUserId: "user-abrio", currentUserName: "El Mismo",
                CancellationToken.None));

        // El caso sigue Pending.
        var entity = await ctx.PartialCreditNoteReconciliations.AsNoTracking().SingleAsync();
        Assert.Equal(PartialCreditNoteReconciliationStatus.Pending, entity.Status);
    }

    // =========================================================================
    // Resolve: self-close CON bypass (single admin + >=100 chars) -> cierra con flag.
    // =========================================================================

    [Fact]
    public async Task Resolve_SelfClose_WithBypass_ClosesWithFlag()
    {
        var (service, ctx, _, _) = BuildService(bypassApplies: true);
        var publicId = await SeedCaseAsync(ctx, openedByUserId: "user-abrio",
            receipts: new[] { (300m, PaymentReceiptStatuses.Voided) });

        var notes = new string('x', 120); // el evaluator mockeado ya devuelve true igual

        var result = await service.ResolveAsync(publicId,
            new ResolvePartialCreditNoteReconciliationRequest { Notes = notes },
            currentUserId: "user-abrio", currentUserName: "Unico Admin",
            CancellationToken.None);

        Assert.Equal("Resolved", result.Status);
        Assert.True(result.FourEyesBypassApplied);
    }

    // =========================================================================
    // Resolve: recibos vivos + sin notas -> 409 (R4 exige justificar).
    // =========================================================================

    [Fact]
    public async Task Resolve_LiveReceipts_WithoutNotes_Throws()
    {
        var (service, ctx, _, _) = BuildService();
        // Recibo todavia Issued (plata no devuelta).
        var publicId = await SeedCaseAsync(ctx, openedByUserId: "user-abrio",
            receipts: new[] { (300m, PaymentReceiptStatuses.Issued) });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveAsync(publicId,
                new ResolvePartialCreditNoteReconciliationRequest { Notes = null },
                currentUserId: "user-otro", currentUserName: "Otro",
                CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_LiveReceipts_WithNotes_ClosesWithFlag()
    {
        var (service, ctx, _, _) = BuildService();
        var publicId = await SeedCaseAsync(ctx, openedByUserId: "user-abrio",
            receipts: new[] { (300m, PaymentReceiptStatuses.Issued) });

        var result = await service.ResolveAsync(publicId,
            new ResolvePartialCreditNoteReconciliationRequest { Notes = "Queda como saldo a favor del cliente en su cta cte." },
            currentUserId: "user-otro", currentUserName: "Otro",
            CancellationToken.None);

        Assert.Equal("Resolved", result.Status);
        Assert.True(result.ClosedWithLiveReceipts);
    }

    // =========================================================================
    // List: filtro pending + estado VIGENTE leido en vivo.
    // =========================================================================

    [Fact]
    public async Task List_Pending_ReturnsLiveReceiptStatus()
    {
        var (service, ctx, _, _) = BuildService();
        // Snapshot dice Issued, pero el recibo real esta Voided -> currentStatus debe ser Voided.
        await SeedCaseAsync(ctx, openedByUserId: "user-abrio",
            receipts: new[] { (300m, PaymentReceiptStatuses.Voided), (400m, PaymentReceiptStatuses.Issued) });

        var result = await service.ListAsync(
            new PartialCreditNoteReconciliationListQuery { Status = "pending" },
            CancellationToken.None);

        Assert.Single(result.Items);
        var dto = result.Items[0];
        Assert.Equal("Pending", dto.Status);
        Assert.Equal(2, dto.Receipts.Count);
        // El estado vigente refleja el PaymentReceipt real, no el snapshot.
        var voided = dto.Receipts.Single(r => r.CurrentStatus == PaymentReceiptStatuses.Voided);
        Assert.Equal(PaymentReceiptStatuses.Issued, voided.StatusAtOpen);
    }

    [Fact]
    public async Task List_Resolved_ExcludesPending()
    {
        var (service, ctx, _, _) = BuildService();
        await SeedCaseAsync(ctx, openedByUserId: "user-abrio",
            receipts: new[] { (300m, PaymentReceiptStatuses.Voided) });

        var result = await service.ListAsync(
            new PartialCreditNoteReconciliationListQuery { Status = "resolved" },
            CancellationToken.None);

        // El unico caso esta Pending -> el filtro "resolved" no lo trae.
        Assert.Empty(result.Items);
    }
}
