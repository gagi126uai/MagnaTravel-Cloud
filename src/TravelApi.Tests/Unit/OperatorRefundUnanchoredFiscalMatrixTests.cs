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
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "anular sin factura" (2026-07-23), item 5(iv) del fix — hallazgo del sweep de "lectores que tocamos".
///
/// <para><b>El bug que este archivo pinea</b>: <c>OperatorRefundService.ValidateFiscalMatrix</c> normalizaba la
/// condicion fiscal de agencia/operador leyendo <c>bc.FiscalSnapshot</c>. Un BC SIN ancla fiscal NUNCA completa
/// ese snapshot (no hay evento fiscal que fotografiar), asi que esos dos valores llegaban siempre <c>null</c> ->
/// <c>TaxConditionNormalizer.Normalize(null)</c> da <c>Unknown</c> -> el PRIMER chequeo del metodo tiraba
/// INV-118 SIEMPRE, sin importar si habia deducciones o no. Registrar CUALQUIER reembolso (incluso sin
/// deducciones fiscales) sobre un BC sin ancla hubiera quedado bloqueado — el mismo callejon sin salida que
/// toda esta obra vino a cerrar, reaparecido un paso mas adelante.</para>
///
/// <para>El fix: sin ancla fiscal, la matriz Mono/RI no aplica (no hay snapshot que validar), pero las
/// retenciones impositivas (kind 10..39) siguen bloqueadas con un mensaje claro — una retencion exige un
/// comprobante fiscal real detras, y sin factura no hay ninguno.</para>
/// </summary>
public class OperatorRefundUnanchoredFiscalMatrixTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"unanchored-fiscal-matrix-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (OperatorRefundService service, Mock<IClientCreditService> clientCreditMock) BuildService(AppDbContext ctx)
    {
        var bcServiceMock = new Mock<IBookingCancellationService>();
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

        var service = new OperatorRefundService(
            ctx, bcServiceMock.Object, clientCreditMock.Object, new Mock<IAuditService>().Object,
            settingsMock.Object, NullLogger<OperatorRefundService>.Instance);

        return (service, clientCreditMock);
    }

    /// <summary>
    /// BC sin ancla fiscal (OriginatingInvoiceId null), en AwaitingOperatorRefund (el salto que hace
    /// PromoteUnanchoredBcToAwaitingOperatorRefundIfNeeded), con snapshot vacio (Source=Unset, tal como lo deja
    /// GetOrCreateServiceCancellationBcAndLineAsync). UNA linea del operador con cap holgado.
    /// </summary>
    private static async Task<(Customer Customer, Supplier Supplier, BookingCancellation Bc)> SeedUnanchoredBcAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "F-NOANCHOR-FM", Name = "Reserva", PayerId = customer.Id, Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = null, // sin ancla fiscal a proposito
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion sin factura",
            DraftedByUserId = "vendedor-1",
            // Snapshot VACIO: es exactamente lo que deja GetOrCreateServiceCancellationBcAndLineAsync para un
            // BC sin ancla — nunca se completa (no hay evento fiscal).
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 50_000m,
            RefundCap = 50_000m,
            ReceivedRefundAmount = 0m,
        });
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (customer, supplier, bc);
    }

    // ============================================================
    // CASO CENTRAL DEL FIX: sin deducciones, el registro PASA (antes de este fix, tiraba INV-118 igual).
    // ============================================================
    [Fact]
    public async Task UnanchoredBc_NoDeductions_AllocateSucceeds_DoesNotThrowINV118()
    {
        await using var ctx = NewDbContext();
        var (customer, supplier, bc) = await SeedUnanchoredBcAsync(ctx);
        var (service, clientCreditMock) = BuildService(ctx);

        var refund = await service.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplier.PublicId, 50_000m, "ARS", DateTime.UtcNow, "Transferencia", "OP-1", null),
            "cajero-1", "Cajero", CancellationToken.None);

        var allocation = await service.AllocateAsync(
            refund.PublicId,
            new AllocateRefundRequest(bc.PublicId, GrossAmount: 50_000m, Deductions: new List<DeductionLineRequest>()),
            "cajero-1", "Cajero", CancellationToken.None);

        Assert.Equal(50_000m, allocation.NetAmount);
        clientCreditMock.Verify(s => s.CreateEntryAsync(
            bc.Id, It.IsAny<OperatorRefundAllocation>(), customer.Id, 50_000m, "ARS",
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ============================================================
    // Deduccion NO fiscal (gasto administrativo, kind < 10) sobre un BC sin ancla -> PASA, se descuenta normal.
    // ============================================================
    [Fact]
    public async Task UnanchoredBc_NonFiscalDeduction_Passes_NetsCorrectly()
    {
        await using var ctx = NewDbContext();
        var (_, supplier, bc) = await SeedUnanchoredBcAsync(ctx);
        var (service, _) = BuildService(ctx);

        var refund = await service.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplier.PublicId, 50_000m, "ARS", DateTime.UtcNow, "Transferencia", "OP-2", null),
            "cajero-1", "Cajero", CancellationToken.None);

        var deduction = new DeductionLineRequest(
            Kind: DeductionKind.AdministrativeFee, Amount: 2_000m, Description: "Gasto administrativo",
            CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null, Jurisdiction: null,
            ForeignCountryCode: null, SupportingDocumentRef: null, JustificationComment: null,
            MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false);

        var allocation = await service.AllocateAsync(
            refund.PublicId,
            new AllocateRefundRequest(bc.PublicId, GrossAmount: 50_000m, Deductions: new List<DeductionLineRequest> { deduction }),
            "cajero-1", "Cajero", CancellationToken.None);

        Assert.Equal(50_000m, allocation.GrossAmount);
        Assert.Equal(48_000m, allocation.NetAmount); // 50.000 - 2.000 de gasto administrativo
    }

    // ============================================================
    // Deduccion FISCAL (retencion impositiva, kind 10..39) sobre un BC sin ancla -> RECHAZADA con mensaje claro,
    // SIN mutar nada (no se registra el ingreso a medias).
    // ============================================================
    [Fact]
    public async Task UnanchoredBc_WithholdingDeduction_Rejects_WithClearMessage_NoOrphanMutation()
    {
        await using var ctx = NewDbContext();
        var (_, supplier, bc) = await SeedUnanchoredBcAsync(ctx);
        var (service, _) = BuildService(ctx);

        var refund = await service.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplier.PublicId, 50_000m, "ARS", DateTime.UtcNow, "Transferencia", "OP-3", null),
            "cajero-1", "Cajero", CancellationToken.None);

        var withholding = new DeductionLineRequest(
            Kind: DeductionKind.IvaWithholding, Amount: 1_000m, Description: "Retencion IVA",
            CertificateNumber: "CERT-1", CertificateDate: DateTime.UtcNow, CertificatePdfUrl: null,
            Jurisdiction: null, ForeignCountryCode: null, SupportingDocumentRef: null, JustificationComment: null,
            MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.AllocateAsync(
                refund.PublicId,
                new AllocateRefundRequest(bc.PublicId, GrossAmount: 50_000m, Deductions: new List<DeductionLineRequest> { withholding }),
                "cajero-1", "Cajero", CancellationToken.None));

        Assert.Equal("INV-BC-NOANCHOR-WITHHOLDING", ex.InvariantCode);
        Assert.Contains("factura de venta", ex.Message, StringComparison.OrdinalIgnoreCase);

        // La allocation NO se creo (el registro del ingreso fisico si quedo -> RecordReceivedAsync corrio
        // antes y es un paso separado del atajo de 2 pasos; lo que importa es que NO quedo imputado a medias).
        Assert.Empty(await ctx.OperatorRefundAllocations.AsNoTracking().ToListAsync());
        var refundReloaded = await ctx.OperatorRefundReceived.AsNoTracking().SingleAsync();
        Assert.Equal(0m, refundReloaded.AllocatedAmount);
    }

    // ============================================================
    // N1 (hallazgo del review, 2026-07-23): ForeignTax (kind 40) vive FUERA del rango 10..39 de "retenciones
    // AR" (no genera credito fiscal AR), pero SIGUE atado a un comprobante fiscal — igual que las retenciones,
    // debe rechazarse sobre un BC sin ancla. Antes de este fix se colaba sin validar (agujero puntual).
    // ============================================================
    [Fact]
    public async Task UnanchoredBc_ForeignTaxDeduction_Rejects_WithClearMessage_NoOrphanMutation()
    {
        await using var ctx = NewDbContext();
        var (_, supplier, bc) = await SeedUnanchoredBcAsync(ctx);
        var (service, _) = BuildService(ctx);

        var refund = await service.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplier.PublicId, 50_000m, "ARS", DateTime.UtcNow, "Transferencia", "OP-4", null),
            "cajero-1", "Cajero", CancellationToken.None);

        var foreignTax = new DeductionLineRequest(
            Kind: DeductionKind.ForeignTax, Amount: 1_500m, Description: "Impuesto del pais del operador",
            CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null, Jurisdiction: null,
            ForeignCountryCode: "BR", SupportingDocumentRef: null, JustificationComment: null,
            MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: false);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.AllocateAsync(
                refund.PublicId,
                new AllocateRefundRequest(bc.PublicId, GrossAmount: 50_000m, Deductions: new List<DeductionLineRequest> { foreignTax }),
                "cajero-1", "Cajero", CancellationToken.None));

        Assert.Equal("INV-BC-NOANCHOR-WITHHOLDING", ex.InvariantCode);
        Assert.Contains("factura de venta", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(await ctx.OperatorRefundAllocations.AsNoTracking().ToListAsync());
        var refundReloaded = await ctx.OperatorRefundReceived.AsNoTracking().SingleAsync();
        Assert.Equal(0m, refundReloaded.AllocatedAmount);
    }

    // ============================================================
    // Kind 99 (Other, cajon de sastre no-fiscal) sigue pasando: confirma que el rango extendido (10..40) no
    // sobre-bloqueo el kind que esta deliberadamente FUERA de el.
    // ============================================================
    [Fact]
    public async Task UnanchoredBc_OtherKindDeduction_StillPasses()
    {
        await using var ctx = NewDbContext();
        var (_, supplier, bc) = await SeedUnanchoredBcAsync(ctx);
        var (service, _) = BuildService(ctx);

        var refund = await service.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplier.PublicId, 50_000m, "ARS", DateTime.UtcNow, "Transferencia", "OP-5", null),
            "cajero-1", "Cajero", CancellationToken.None);

        var other = new DeductionLineRequest(
            Kind: DeductionKind.Other, Amount: 500m, Description: "Ajuste varios",
            CertificateNumber: null, CertificateDate: null, CertificatePdfUrl: null, Jurisdiction: null,
            ForeignCountryCode: null, SupportingDocumentRef: null, JustificationComment: "Motivo del ajuste",
            MissingFiscalSupport: false, Comment: null, RequiresAccountingReview: true);

        var allocation = await service.AllocateAsync(
            refund.PublicId,
            new AllocateRefundRequest(bc.PublicId, GrossAmount: 50_000m, Deductions: new List<DeductionLineRequest> { other }),
            "cajero-1", "Cajero", CancellationToken.None);

        Assert.Equal(49_500m, allocation.NetAmount); // 50.000 - 500 de ajuste, sin tocar la matriz fiscal.
    }
}
