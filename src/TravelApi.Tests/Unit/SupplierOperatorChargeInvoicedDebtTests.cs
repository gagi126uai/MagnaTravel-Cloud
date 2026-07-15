using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-07-15) La obra "el cargo facturado aparte del operador suma al saldo OFICIAL". Hasta esta fecha, un
/// cargo <see cref="PenaltyCollectionMode.FacturadaAparte"/> (el operador devuelve el reembolso INTEGRO pero
/// factura su multa con su propio documento) solo aparecia en el EXTRACTO del proveedor, nunca en
/// <c>Supplier.CurrentBalance</c> / <c>SupplierBalanceByCurrency</c> (el "Saldo (deuda)" del listado, el
/// semaforo y el total global "Cuentas por pagar"). Esta suite pinea:
///   1) el persister suma el cargo facturado aparte al saldo (compra 100 + cargo 50 -> 150);
///   2) un cargo en una cancelacion ABORTADA no cuenta (evento sin efecto);
///   3) una multa RETENIDA de un reembolso NO suma (ya esta neteada en el RefundCap del operador);
///   4) multimoneda: cada moneda su balance, sin cruzarse;
///   5) el reconciler del pool de saldo a favor NO cuenta el cargo dos veces (el fix critico).
/// DbContext InMemory, sin Docker (la DB real vive en el VPS).
/// </summary>
public class SupplierOperatorChargeInvoicedDebtTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"op-charge-invoiced-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<Supplier> AddSupplierAsync(AppDbContext ctx)
    {
        var supplier = new Supplier { Name = "Operador", InvoicingMode = SupplierInvoicingMode.TotalToCustomer, IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();
        return supplier;
    }

    private static async Task<(Reserva Reserva, Customer Customer)> AddReservaAsync(AppDbContext ctx, string numero)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        var reserva = new Reserva { NumeroReserva = numero, Name = "Reserva " + numero, PayerId = customer.Id, Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return (reserva, customer);
    }

    // Hotel "Confirmado" (cuenta como compra por la regla oficial) del proveedor en la reserva.
    private static void AddConfirmedHotel(AppDbContext ctx, int supplierId, int reservaId, decimal netCost, string currency)
    {
        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaId, SupplierId = supplierId, HotelName = "Hotel", City = "Ciudad",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12), Nights = 2,
            Status = "Confirmado", NetCost = netCost, SalePrice = netCost * 1.5m, Currency = currency
        });
    }

    /// <summary>
    /// Siembra una cancelacion con UNA linea del operador y UN cargo con el modo indicado. RefundCap=0 para que
    /// el receivable "me tiene que devolver" (Y) sea 0 (no interfiere con los numeros del saldo). Usa el
    /// centinela legacy (Generic, ServiceId=0) con ConfirmedWithClientAt=null: asi el servicio "sigue contando"
    /// y no genera Y — es la forma mas limpia de aislar el cargo facturado aparte sin sembrar un servicio real.
    /// </summary>
    private static async Task<BookingCancellationLineOperatorCharge> SeedCancellationChargeAsync(
        AppDbContext ctx, int supplierId, int reservaId, int customerId,
        BookingCancellationStatus status,
        PenaltyCollectionMode collectionMode,
        decimal chargeAmount,
        string currency)
    {
        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reservaId,
            CustomerId = customerId,
            SupplierId = supplierId,
            OriginatingInvoiceId = invoice.Id,
            Status = status,
            ConfirmedWithClientAt = null,
            Reason = "anulacion con reembolso integro + cargo facturado aparte",
            DraftedByUserId = "v",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = supplierId,
            ServiceTable = CancellableServiceTable.Generic,
            ServiceId = 0,
            Scope = BookingCancellationLineScope.Full,
            Currency = currency,
            LineSaleAmount = chargeAmount,
            RefundCap = 0m,
            ReceivedRefundAmount = 0m,
            RetainedDeductionAmount = 0m,
        };
        ctx.Set<BookingCancellationLine>().Add(line);
        await ctx.SaveChangesAsync();

        var charge = new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = line.Id,
            Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = collectionMode,
            Amount = chargeAmount,
            Currency = currency,
            DocumentRef = collectionMode == PenaltyCollectionMode.FacturadaAparte ? "FAC-OP-001" : null,
            ConfirmedByUserId = "v",
        };
        ctx.Set<BookingCancellationLineOperatorCharge>().Add(charge);
        await ctx.SaveChangesAsync();
        return charge;
    }

    private static async Task<decimal> PersistedBalanceAsync(AppDbContext ctx, int supplierId, string currency)
    {
        await SupplierDebtPersister.PersistAsync(ctx, supplierId, CancellationToken.None);
        await ctx.SaveChangesAsync();
        var row = await ctx.SupplierBalanceByCurrency
            .FirstOrDefaultAsync(r => r.SupplierId == supplierId && r.Currency == currency);
        return row?.Balance ?? 0m;
    }

    // ============================= (1) compra + cargo facturado aparte =============================

    [Fact]
    public async Task Persister_ConfirmedPurchasePlusInvoicedCharge_BalanceIncludesBoth()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "F-INV-1");
        AddConfirmedHotel(ctx, supplier.Id, reserva.Id, netCost: 100m, currency: "ARS");
        await ctx.SaveChangesAsync();

        await SeedCancellationChargeAsync(
            ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyCollectionMode.FacturadaAparte, chargeAmount: 50m, currency: "ARS");

        await SupplierDebtPersister.PersistAsync(ctx, supplier.Id, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var row = await ctx.SupplierBalanceByCurrency.SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == "ARS");
        Assert.Equal(100m, row.ConfirmedPurchases);
        Assert.Equal(50m, row.OperatorChargesInvoiced);
        Assert.Equal(0m, row.TotalPaid);
        Assert.Equal(150m, row.Balance);   // 100 compra + 50 cargo facturado aparte
    }

    // ============================= (2) cargo en cancelacion abortada no cuenta =============================

    [Fact]
    public async Task Persister_InvoicedChargeOnAbortedCancellation_DoesNotCount()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "F-INV-2");
        AddConfirmedHotel(ctx, supplier.Id, reserva.Id, netCost: 100m, currency: "ARS");
        await ctx.SaveChangesAsync();

        // El evento entero quedo sin efecto (Aborted): su cargo no es deuda viva.
        await SeedCancellationChargeAsync(
            ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.Aborted,
            PenaltyCollectionMode.FacturadaAparte, chargeAmount: 50m, currency: "ARS");

        var balance = await PersistedBalanceAsync(ctx, supplier.Id, "ARS");
        Assert.Equal(100m, balance);   // solo la compra; el cargo de la BC abortada no suma

        var row = await ctx.SupplierBalanceByCurrency.SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == "ARS");
        Assert.Equal(0m, row.OperatorChargesInvoiced);
    }

    // ============================= (3) multa retenida NO suma =============================

    [Fact]
    public async Task Persister_RetainedPenaltyCharge_DoesNotAddToOfficialBalance()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "F-INV-3");
        AddConfirmedHotel(ctx, supplier.Id, reserva.Id, netCost: 100m, currency: "ARS");
        await ctx.SaveChangesAsync();

        // Una multa RETENIDA ya esta neteada en el reembolso esperado del operador (RefundCap): NO es deuda
        // nueva. El reader solo mira FacturadaAparte, asi que esta no debe sumar al saldo oficial.
        await SeedCancellationChargeAsync(
            ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyCollectionMode.Retenida, chargeAmount: 50m, currency: "ARS");

        var balance = await PersistedBalanceAsync(ctx, supplier.Id, "ARS");
        Assert.Equal(100m, balance);   // solo la compra; la multa retenida no infla el saldo oficial

        var row = await ctx.SupplierBalanceByCurrency.SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == "ARS");
        Assert.Equal(0m, row.OperatorChargesInvoiced);
    }

    // ============================= (4) multimoneda separada por moneda =============================

    [Fact]
    public async Task Persister_InvoicedChargesMultiCurrency_SeparateBalances()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "F-INV-4");
        AddConfirmedHotel(ctx, supplier.Id, reserva.Id, netCost: 100m, currency: "ARS");
        AddConfirmedHotel(ctx, supplier.Id, reserva.Id, netCost: 40m, currency: "USD");
        await ctx.SaveChangesAsync();

        await SeedCancellationChargeAsync(
            ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyCollectionMode.FacturadaAparte, chargeAmount: 50m, currency: "ARS");
        await SeedCancellationChargeAsync(
            ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyCollectionMode.FacturadaAparte, chargeAmount: 30m, currency: "USD");

        await SupplierDebtPersister.PersistAsync(ctx, supplier.Id, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var ars = await ctx.SupplierBalanceByCurrency.SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == "ARS");
        var usd = await ctx.SupplierBalanceByCurrency.SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == "USD");
        Assert.Equal(150m, ars.Balance);   // 100 + 50, sin cruzar con USD
        Assert.Equal(70m, usd.Balance);    // 40 + 30, sin cruzar con ARS
    }

    // ============================= (5) coherencia extracto-vs-saldo =============================

    [Fact]
    public async Task PersistedBalance_MatchesExtractLeDebo_WithInvoicedCharge()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "F-INV-5");
        AddConfirmedHotel(ctx, supplier.Id, reserva.Id, netCost: 100m, currency: "ARS");
        await ctx.SaveChangesAsync();

        await SeedCancellationChargeAsync(
            ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyCollectionMode.FacturadaAparte, chargeAmount: 50m, currency: "ARS");

        await SupplierDebtPersister.PersistAsync(ctx, supplier.Id, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var persisted = await ctx.SupplierBalanceByCurrency.SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == "ARS");

        // El "Le debo" del extracto = caja (100) + circuito (cargo facturado aparte 50) = 150, sin receivable Y
        // (RefundCap 0). Debe COINCIDIR con el Balance persistido (100 compra + 50 cargo). Reconstruimos el
        // reconciliador economico con los mismos insumos que usa el extracto.
        var cashClosingByCurrency = new System.Collections.Generic.Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["ARS"] = persisted.ConfirmedPurchases - persisted.TotalPaid, // 100 - 0
        };
        var circuit = await SupplierCancellationCircuitReader.LoadAsync(ctx, supplier.Id, CancellationToken.None);
        var reconciliation = TravelApi.Domain.Reservations.SupplierAccountReconciliationBuilder.Build(
            cashClosingByCurrency, circuit.CircuitLines, circuit.ReceivableByCurrency);

        var block = reconciliation.Currencies.Single(c => c.Currency == "ARS");
        Assert.Equal(150m, persisted.Balance);
        Assert.Equal(persisted.Balance, block.ITheyOwe);   // el "Le debo" del extracto == saldo oficial
        Assert.Equal(0m, block.TheyOweMe);                 // sin reembolso por cobrar (RefundCap 0)
    }

    // ============================= (6) reconciler sin doble-conteo =============================

    [Fact]
    public async Task Reconciler_OverpaymentWithInvoicedCharge_DoesNotDoubleCountCharge()
    {
        await using var ctx = NewContext();
        var supplier = await AddSupplierAsync(ctx);
        var (reserva, customer) = await AddReservaAsync(ctx, "F-INV-6");

        // Sobrepago puro: un pago de 200 sin compras. Caja = 0 - 200 = -200 (saldo a favor bruto).
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = null, Amount = 200m, Currency = "ARS", Method = "Transferencia"
        });
        await ctx.SaveChangesAsync();

        // Ademas un cargo facturado aparte de 50 (deuda nueva). El saldo a favor CONSUMIBLE neto = 200 - 50 = 150.
        await SeedCancellationChargeAsync(
            ctx, supplier.Id, reserva.Id, customer.Id,
            BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyCollectionMode.FacturadaAparte, chargeAmount: 50m, currency: "ARS");

        // El saldo oficial YA incluye el cargo (Balance = 0 + 50 - 200 = -150). Si el reconciler leyera Balance
        // en vez de ConfirmedPurchases - TotalPaid, sumaria el cargo del circuito DE NUEVO (-150 + 50 = -100 ->
        // prepago 100), robandole 50 de pool al operador. El fix lee ConfirmedPurchases - TotalPaid (-200), asi
        // que el prepago correcto es 150.
        await SupplierDebtPersister.PersistAsync(ctx, supplier.Id, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var persisted = await ctx.SupplierBalanceByCurrency.SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == "ARS");
        Assert.Equal(-150m, persisted.Balance);   // 0 + 50 - 200: el cargo ya vive en Balance

        var overpayment = await SupplierCreditReconciler.ComputeConsumableOverpaymentByCurrencyAsync(
            ctx, supplier.Id, CancellationToken.None);
        Assert.True(overpayment.TryGetValue("ARS", out var consumable));
        Assert.Equal(150m, consumable);   // 200 sobrepago - 50 cargo (UNA sola vez), no 100
    }
}
