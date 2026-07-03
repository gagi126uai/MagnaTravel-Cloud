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
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-042 §3.3.2 (C1 / S2 review, 2026-07-02): tests PUNTA A PUNTA del minteo del saldo a favor del cliente
/// desde el reembolso del operador (<see cref="OperatorRefundService"/>), que es donde vive el cableado real de
/// la regla de moneda. Cubre:
/// <list type="bullet">
///   <item>El credito se mintea en la moneda de la OBLIGACION del cliente por el monto DEVUELTO por el operador,
///         acotado por lo devuelto (no puede superar lo que entro). En el caso normal ese devuelto es &lt;= lo
///         cobrado al cliente (test lo verifica con cobrado &gt; devuelto).</item>
///   <item>DIVERGENCIA (operador reembolsa en una moneda distinta de la obligacion del cliente) -> revision
///         manual (INV-042-CREDIT-CURRENCY), NO se mintea en la moneda equivocada.</item>
/// </list>
///
/// <para>Nota: el TOPE duro del monto lo aplica el CHECK SQL del operator-refund (AllocatedAmount &lt;=
/// ReceivedAmount), que InMemory no evalua; aca verificamos que el credito minteado = lo devuelto por el
/// operador (fuente unica del monto), que es lo que ese cap acota. Ver el comentario del sitio de minteo.</para>
/// </summary>
public class Adr042ClientCreditCurrencyTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr042-credit-currency-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (OperatorRefundService service, Mock<IClientCreditService> clientCreditMock) BuildService(AppDbContext ctx)
    {
        var bcServiceMock = new Mock<IBookingCancellationService>();
        var clientCreditMock = new Mock<IClientCreditService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();

        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });
        bcServiceMock.Setup(s => s.OnAllocationRecordedAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clientCreditMock.Setup(s => s.CreateEntryAsync(
                It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientCreditEntry());

        var service = new OperatorRefundService(
            ctx, bcServiceMock.Object, clientCreditMock.Object, auditMock.Object,
            settingsMock.Object, NullLogger<OperatorRefundService>.Instance);
        return (service, clientCreditMock);
    }

    /// <summary>Siembra BC AwaitingOperatorRefund con una linea del operador en <paramref name="lineCurrency"/>
    /// y (opcional) un pago del cliente imputado en <paramref name="clientPaymentCurrency"/> por <paramref name="clientPaid"/>.</summary>
    private static async Task<(Guid supplierPublicId, Guid bcPublicId, int customerId)> SeedAsync(
        AppDbContext ctx, string lineCurrency, string? clientPaymentCurrency, decimal clientPaid)
    {
        var customer = new Customer { FullName = "Cliente ADR042", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-042C", Name = "Reserva credito", PayerId = customer.Id, Status = EstadoReserva.Cancelled };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice { TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 600, ImporteTotal = 1_000m, ReservaId = reserva.Id };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        if (clientPaymentCurrency is not null && clientPaid > 0m)
        {
            // Cobro del cliente imputado (la obligacion): su ImputedCurrency es el discriminador (ADR-021).
            ctx.Payments.Add(new Payment
            {
                ReservaId = reserva.Id,
                Amount = clientPaid,
                Currency = clientPaymentCurrency,
                ImputedCurrency = clientPaymentCurrency,
                ImputedAmount = clientPaid,
                Status = "Paid",
            });
            await ctx.SaveChangesAsync();
        }

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "cancelacion con reembolso esperado", DraftedByUserId = "vendedor-1",
            AmountPaidAtCancellation = clientPaid, EstimatedRefundAmount = 0m, ReceivedRefundAmount = 0m,
            OperatorRefundDueBy = DateTime.UtcNow.AddDays(30),
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = lineCurrency,
                AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
                SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
                Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m, FetchedAt = DateTime.UtcNow,
            },
        };
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = lineCurrency,
            LineSaleAmount = 1_000m, RefundCap = 1_000m, ReceivedRefundAmount = 0m,
        });
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (supplier.PublicId, bc.PublicId, customer.Id);
    }

    private static RecordAndAllocateRefundRequest NewRequest(Guid supplierPublicId, Guid bcPublicId, decimal amount, string currency) =>
        new(
            SupplierPublicId: supplierPublicId,
            BookingCancellationPublicId: bcPublicId,
            ReceivedAmount: amount,
            Currency: currency,
            ReceivedAt: DateTime.UtcNow,
            Method: "Transfer",
            Reference: "Op-1",
            Notes: "Reembolso",
            IdempotencyKey: Guid.NewGuid());

    [Fact]
    public async Task Credito_seMinteaEnMonedaDeObligacion_PorLoDevuelto_NoSuperaLoCobrado()
    {
        // Cliente pago (cobrado) 1000 USD; el operador devuelve 700 USD (obligacion USD, circuito USD).
        await using var ctx = NewDbContext();
        var (supplierId, bcId, customerId) = await SeedAsync(ctx, lineCurrency: "USD", clientPaymentCurrency: "USD", clientPaid: 1_000m);
        var (service, clientCreditMock) = BuildService(ctx);

        await service.RecordAndAllocateAsync(
            NewRequest(supplierId, bcId, amount: 700m, currency: "USD"), "cajero-1", "Cajero", CancellationToken.None);

        // El credito se mintea en la moneda de la obligacion (USD) por lo DEVUELTO (700), que es <= lo cobrado (1000).
        clientCreditMock.Verify(s => s.CreateEntryAsync(
            It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), customerId, 700m, "USD",
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Divergencia_ReembolsoEnMonedaDistintaDeLaObligacion_VaARevisionManual_NoMintea()
    {
        // Circuito del operador en USD (la linea es USD, el reembolso USD pasa INV-118), pero la OBLIGACION del
        // cliente esta imputada en ARS. El reembolso USD diverge de la obligacion ARS -> revision manual.
        await using var ctx = NewDbContext();
        var (supplierId, bcId, _) = await SeedAsync(ctx, lineCurrency: "USD", clientPaymentCurrency: "ARS", clientPaid: 150_000m);
        var (service, clientCreditMock) = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.RecordAndAllocateAsync(
                NewRequest(supplierId, bcId, amount: 700m, currency: "USD"), "cajero-1", "Cajero", CancellationToken.None));

        Assert.Equal("INV-042-CREDIT-CURRENCY", ex.InvariantCode);
        // No se minteo saldo a favor en la moneda equivocada.
        clientCreditMock.Verify(s => s.CreateEntryAsync(
            It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
