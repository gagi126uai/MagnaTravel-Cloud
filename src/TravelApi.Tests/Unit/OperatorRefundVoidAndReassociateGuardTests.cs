using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Circuito proveedor, Tanda "deshacer un reembolso mal cargado" (2026-07-21): el backend YA tenia
/// <c>VoidAllocationAsync</c> y <c>ReassociateAllocationAsync</c> completos y expuestos por
/// <c>OperatorRefundsController</c> (DELETE /operator-refunds/allocations/{id} y PATCH
/// .../reassociate). Lo que faltaba cubrir con un test era el guard "el cliente ya gasto ese saldo
/// a favor" — antes tiraba un mensaje con nombres de clase internos (<c>ClientRefundReversal</c>,
/// "allocation"), prohibido por el gate de exposicion de datos apenas el dia que el frontend
/// conecte el boton. Estos dos tests fijan el mensaje NUEVO (criollo, sin jerga) para que no vuelva
/// a filtrarse.
///
/// <para>Tests UNIT con EF InMemory: alcanza porque el guard se dispara ANTES de cualquier
/// escritura (paso 3 de 9 en <c>TryVoidOnceAsync</c>/<c>TryReassociateOnceAsync</c>), no hace falta
/// Postgres real para este caso.</para>
/// </summary>
public class OperatorRefundVoidAndReassociateGuardTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"void-reassociate-guard-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static OperatorRefundService BuildService(AppDbContext ctx)
    {
        var bcServiceMock = new Mock<IBookingCancellationService>();
        var clientCreditMock = new Mock<IClientCreditService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();

        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });

        return new OperatorRefundService(
            ctx, bcServiceMock.Object, clientCreditMock.Object, auditMock.Object,
            settingsMock.Object, NullLogger<OperatorRefundService>.Instance);
    }

    /// <summary>
    /// Siembra un reembolso YA imputado (allocation viva) con un saldo a favor del cliente que
    /// tiene un retiro CONSUMIDO (Transfer, no KeptAsCredit) — el escenario que dispara el guard.
    /// </summary>
    private static async Task<(OperatorRefundAllocation Allocation, BookingCancellation NewBc)> SeedConsumedCreditAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente con saldo gastado", IsActive = true };
        var supplier = new Supplier { Name = "Operador Guard", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-GUARD-1", Name = "Reserva guard", PayerId = customer.Id };
        var reservaDestino = new Reserva { NumeroReserva = "R-GUARD-2", Name = "Reserva guard destino", PayerId = customer.Id };
        ctx.Reservas.AddRange(reserva, reservaDestino);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice { TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 700, ImporteTotal = 500m, ReservaId = reserva.Id };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var snapshot = new FiscalSnapshot
        {
            CurrencyAtEvent = "ARS",
            AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
            SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
            Source = ExchangeRateSource.Manual,
            ExchangeRateAtOriginalInvoice = 1m,
            FetchedAt = DateTime.UtcNow,
        };

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.ClientCreditApplied,
            Reason = "Cancelacion con reembolso ya gastado por el cliente",
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = snapshot,
        };
        // Destino alternativo para el test de reassociate (mismo estado imputable, misma moneda).
        var newBc = new BookingCancellation
        {
            ReservaId = reservaDestino.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion destino para reasociar",
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = snapshot,
        };
        ctx.BookingCancellations.AddRange(bc, newBc);
        await ctx.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id,
            ReceivedAmount = 500m,
            AllocatedAmount = 500m,
            Currency = "ARS",
            ReceivedByUserId = "cajero-1",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc.Id,
            GrossAmount = 500m,
            NetAmount = 500m,
            CreatedByUserId = "cajero-1",
        };
        ctx.OperatorRefundAllocations.Add(allocation);
        await ctx.SaveChangesAsync();

        // El saldo a favor que origino esta allocation ya tiene un retiro por TRANSFERENCIA: el
        // cliente ya se llevo esa plata, no es un saldo "intacto" que se pueda simplemente deshacer.
        var creditEntry = new ClientCreditEntry
        {
            CustomerId = customer.Id,
            OperatorRefundAllocationId = allocation.Id,
            BookingCancellationId = bc.Id,
            Currency = "ARS",
            CreditedAmount = 500m,
            RemainingBalance = 0m,
        };
        creditEntry.Withdrawals.Add(new ClientCreditWithdrawal
        {
            Amount = 500m,
            Kind = WithdrawalKind.Transfer,
            ExecutedByUserId = "cajero-1",
            ExecutedByUserName = "Cajero Uno",
        });
        ctx.ClientCreditEntries.Add(creditEntry);
        await ctx.SaveChangesAsync();

        return (allocation, newBc);
    }

    [Fact]
    public async Task VoidAllocation_WhenClientAlreadySpentTheCredit_RejectsWithPlainSpanishMessage_NoInternalClassNames()
    {
        await using var ctx = NewDbContext();
        var (allocation, _) = await SeedConsumedCreditAsync(ctx);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VoidAllocationAsync(
                allocation.PublicId,
                new VoidAllocationRequest("Intento de deshacer un reembolso ya consumido por el cliente."),
                userId: "cajero-1", userName: "Cajero Uno", ct: CancellationToken.None));

        // El mensaje explica la situacion en criollo y no menciona nombres de clase internos.
        Assert.DoesNotContain("ClientRefundReversal", ex.Message);
        Assert.DoesNotContain("allocation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("saldo a favor", ex.Message);
        Assert.Contains("requiere autorización", ex.Message);

        // No debe haber mutado nada: la allocation sigue viva.
        var reloaded = await ctx.OperatorRefundAllocations.AsNoTracking().SingleAsync(a => a.Id == allocation.Id);
        Assert.False(reloaded.IsVoided);
    }

    [Fact]
    public async Task ReassociateAllocation_WhenClientAlreadySpentTheCredit_RejectsWithPlainSpanishMessage_NoInternalClassNames()
    {
        await using var ctx = NewDbContext();
        var (allocation, newBc) = await SeedConsumedCreditAsync(ctx);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReassociateAllocationAsync(
                allocation.PublicId,
                new ReassociateAllocationRequest(newBc.PublicId, "Intento de mover un reembolso ya consumido por el cliente."),
                userId: "cajero-1", userName: "Cajero Uno", ct: CancellationToken.None));

        Assert.DoesNotContain("ClientRefundReversal", ex.Message);
        Assert.DoesNotContain("allocation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("saldo a favor", ex.Message);
        Assert.Contains("requiere autorización", ex.Message);

        // No debe haber mutado nada: la allocation vieja sigue apuntando al BC original.
        var reloaded = await ctx.OperatorRefundAllocations.AsNoTracking().SingleAsync(a => a.Id == allocation.Id);
        Assert.False(reloaded.IsVoided);
        Assert.Single(await ctx.OperatorRefundAllocations.ToListAsync());
    }

    /// <summary>
    /// Siembra una allocation que YA esta anulada (IsVoided=true), sin saldo a favor asociado — el
    /// escenario minimo para disparar el guard de "doble anulacion" / "reasociar algo ya anulado".
    /// A diferencia de <see cref="SeedConsumedCreditAsync"/>, este guard se dispara en el paso 2 (antes
    /// de mirar el ClientCreditEntry), asi que no hace falta sembrar un retiro consumido.
    /// </summary>
    private static async Task<(OperatorRefundAllocation Allocation, BookingCancellation NewBc)> SeedAlreadyVoidedAllocationAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente con reembolso ya anulado", IsActive = true };
        var supplier = new Supplier { Name = "Operador Guard Voided", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-GUARD-VOIDED-1", Name = "Reserva guard voided", PayerId = customer.Id };
        var reservaDestino = new Reserva { NumeroReserva = "R-GUARD-VOIDED-2", Name = "Reserva guard voided destino", PayerId = customer.Id };
        ctx.Reservas.AddRange(reserva, reservaDestino);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice { TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 701, ImporteTotal = 500m, ReservaId = reserva.Id };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var snapshot = new FiscalSnapshot
        {
            CurrencyAtEvent = "ARS",
            AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
            SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
            Source = ExchangeRateSource.Manual,
            ExchangeRateAtOriginalInvoice = 1m,
            FetchedAt = DateTime.UtcNow,
        };

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.ClientCreditApplied,
            Reason = "Cancelacion con reembolso ya anulado por error de carga",
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = snapshot,
        };
        var newBc = new BookingCancellation
        {
            ReservaId = reservaDestino.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion destino para reasociar (caso ya anulado)",
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = snapshot,
        };
        ctx.BookingCancellations.AddRange(bc, newBc);
        await ctx.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id,
            ReceivedAmount = 500m,
            AllocatedAmount = 0m,
            Currency = "ARS",
            ReceivedByUserId = "cajero-1",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        // La allocation nace YA anulada: es lo unico que necesitamos para que el guard del paso 2
        // (antes de tocar ClientCreditEntry) dispare.
        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc.Id,
            GrossAmount = 500m,
            NetAmount = 500m,
            CreatedByUserId = "cajero-1",
            IsVoided = true,
            VoidedAt = DateTime.UtcNow,
            VoidedByUserId = "cajero-1",
            VoidedReason = "Anulada previamente por error de carga (seed de test).",
        };
        ctx.OperatorRefundAllocations.Add(allocation);
        await ctx.SaveChangesAsync();

        return (allocation, newBc);
    }

    [Fact]
    public async Task VoidAllocation_WhenAlreadyVoided_RejectsWithPlainSpanishMessage_NoInternalNamesOrCodes()
    {
        await using var ctx = NewDbContext();
        var (allocation, _) = await SeedAlreadyVoidedAllocationAsync(ctx);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<TravelApi.Domain.Exceptions.BusinessInvariantViolationException>(() =>
            service.VoidAllocationAsync(
                allocation.PublicId,
                new VoidAllocationRequest("Intento de anular dos veces el mismo reembolso por error."),
                userId: "cajero-1", userName: "Cajero Uno", ct: CancellationToken.None));

        // El mensaje es el nuevo texto en criollo, sin jerga interna. El invariantCode ("INV-093") vive
        // en la propiedad tipada de la excepcion para logs — no debe aparecer dentro del texto del mensaje.
        Assert.DoesNotContain("allocation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INV-", ex.Message);
        Assert.Contains("ya estaba deshecho", ex.Message);
    }

    [Fact]
    public async Task ReassociateAllocation_WhenAlreadyVoided_RejectsWithPlainSpanishMessage_NoInternalNamesOrCodes()
    {
        await using var ctx = NewDbContext();
        var (allocation, newBc) = await SeedAlreadyVoidedAllocationAsync(ctx);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<TravelApi.Domain.Exceptions.BusinessInvariantViolationException>(() =>
            service.ReassociateAllocationAsync(
                allocation.PublicId,
                new ReassociateAllocationRequest(newBc.PublicId, "Intento de reasociar un reembolso ya anulado por error."),
                userId: "cajero-1", userName: "Cajero Uno", ct: CancellationToken.None));

        Assert.DoesNotContain("allocation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INV-", ex.Message);
        Assert.Contains("anulado", ex.Message);
        Assert.Contains("no se puede mover", ex.Message);
    }
}
