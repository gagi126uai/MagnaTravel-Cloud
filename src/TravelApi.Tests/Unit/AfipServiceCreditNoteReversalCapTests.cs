using System;
using System.Linq;
using System.Net.Http;
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
/// FIX T0 "plata" (2026-07-10, bug confirmado en prod, reserva F-2026-1038): la reversion
/// economica de una NC (<c>AfipService.ApplyCreditNoteEconomicReversalAsync</c>) NO puede superar
/// lo que la factura original REALMENTE cobro. Facturar sin cobrar es legitimo (ADR-037 desacople
/// facturacion/cobranza); antes de este fix, anular una factura sin ningun Payment de cobro
/// generaba igual un Payment de reversion negativo completo -> deuda 100% fantasma (caso real:
/// Invoice 61 / NC 62, $726.000, cero Payments de cobro en toda la reserva).
///
/// <para>Cubre <see cref="AfipService.CalculateCreditNoteReversalCapAsync"/> (privado, se ejercita
/// via <c>ApplyCreditNoteEconomicReversalAsync</c>) en los 5 escenarios de la spec:
/// <list type="number">
///   <item>Factura sin ningun cobro -> NC total -> NO se crea reversal.</item>
///   <item>Factura cobrada parcialmente ($400 de $1000) -> NC total -> reversal por -$400.</item>
///   <item>Pago cruzado ARS/USD -> el cap usa el monto/moneda IMPUTADOS, no la caja real.</item>
///   <item>Dos NC parciales sucesivas -> la 2da solo revierte lo que quedo sin revertir.</item>
///   <item>Regresion: factura totalmente cobrada -> NC total -> reversal completo (comportamiento
///   historico intacto, cubierto tambien por <c>AfipServicePartialCreditNoteReversalTests</c>).</item>
/// </list></para>
/// </summary>
public class AfipServiceCreditNoteReversalCapTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public AfipServiceCreditNoteReversalCapTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    private static AfipService BuildAfipService(AppDbContext context)
        => new(
            context,
            NullLogger<AfipService>.Instance,
            new HttpClient(),
            new NoopSensitiveDataProtector(),
            new Mock<IAuditService>().Object);

    /// <summary>
    /// Semilla minima comun: una Reserva + una factura original (Id fijo 800). El resto (payments,
    /// NC, BC) lo arma cada test segun el escenario que necesita.
    /// </summary>
    private static void SeedReservaYFacturaOriginal(AppDbContext context, decimal originalAmount, string originalMonId = "PES")
    {
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-T0-001",
            Name = "Reserva T0 cap reversion",
            Status = EstadoReserva.Confirmed,
            TotalSale = originalAmount,
            TotalCost = 0m,
            Balance = 0m,
            TotalPaid = originalAmount,
        });

        context.Invoices.Add(new Invoice
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
            MonId = originalMonId,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            AnnulledByUserId = "user-123",
            AnnulledByUserName = "Backoffice",
        });
    }

    private static Invoice BuildCreditNoteInvoice(int id, decimal ncAmount, string ncMonId = "PES")
        => new()
        {
            Id = id,
            ReservaId = 1,
            TipoComprobante = 8,
            PuntoDeVenta = 5,
            NumeroComprobante = 9000 + id,
            Resultado = "A",
            ImporteTotal = ncAmount,
            ImporteNeto = ncAmount,
            ImporteIva = 0m,
            MonId = ncMonId,
            CreatedAt = DateTime.UtcNow,
            OriginalInvoiceId = 800,
        };

    // =========================================================================
    // Escenario 1 (el bug real, F-2026-1038): factura SIN ningun cobro.
    // =========================================================================

    [Fact]
    public async Task NcTotal_FacturaSinNingunCobro_NoCreaReversal()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        // Factura de $726.000 con CAE pero CERO Payments de cobro (facturar sin cobrar, ADR-037).
        SeedReservaYFacturaOriginal(ctx, originalAmount: 726000m);
        ctx.Invoices.Add(BuildCreditNoteInvoice(id: 801, ncAmount: 726000m));
        await ctx.SaveChangesAsync();

        var service = BuildAfipService(ctx);
        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        // El bug original: esto generaba un Payment -726.000 sin ningun cobro que compensar.
        var reversalExists = await ctx.Payments.AsNoTracking()
            .AnyAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.False(reversalExists);
    }

    // =========================================================================
    // Escenario 2: factura cobrada PARCIALMENTE ($400 de $1000) -> NC total -> reversal por -$400.
    // =========================================================================

    [Fact]
    public async Task NcTotal_FacturaCobradaParcialmente_ReversalQuedaTopeadoAlCobradoReal()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        SeedReservaYFacturaOriginal(ctx, originalAmount: 1000m);
        ctx.Invoices.Add(BuildCreditNoteInvoice(id: 801, ncAmount: 1000m));

        // Solo se cobraron $400 de los $1000 facturados (el resto quedo pendiente de cobro,
        // legitimo bajo ADR-037: venta firme facturada sin que el cliente haya terminado de pagar).
        ctx.Payments.Add(new Payment
        {
            Id = 500,
            ReservaId = 1,
            Amount = 400m,
            Currency = "ARS",
            PaidAt = DateTime.UtcNow.AddDays(-3),
            Method = "Transfer",
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true,
            RelatedInvoiceId = 800,
        });
        await ctx.SaveChangesAsync();

        var service = BuildAfipService(ctx);
        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var reversal = await ctx.Payments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal);
        // La reversion NO puede ser -1000 (la NC completa): solo se cobraron 400.
        Assert.Equal(-400m, reversal!.Amount);
    }

    // =========================================================================
    // Escenario 3 (ADR-021, pago cruzado): el cliente pago en ARS pero se imputa contra una
    // factura en USD. El cap tiene que usar el monto/moneda IMPUTADOS, no la caja real en ARS.
    // =========================================================================

    [Fact]
    public async Task NcTotal_PagoCruzadoArsImputadoAUsd_CapUsaMontoImputado()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        // Factura original en USD (MonId ARCA "DOL" -> ISO "USD"), ImporteTotal 1000 (USD).
        SeedReservaYFacturaOriginal(ctx, originalAmount: 1000m, originalMonId: "DOL");
        ctx.Invoices.Add(BuildCreditNoteInvoice(id: 801, ncAmount: 1000m, ncMonId: "DOL"));

        // Pago cruzado: el cliente puso $400.000 ARS (Amount+Currency = la caja REAL), pero eso
        // se imputo como USD 400 contra el saldo de la factura en dolares (ImputedAmount/ImputedCurrency).
        // Lo que bajo la DEUDA de la factura en USD es el monto IMPUTADO, no la caja en ARS.
        ctx.Payments.Add(new Payment
        {
            Id = 500,
            ReservaId = 1,
            Amount = 400000m,
            Currency = "ARS",
            ImputedCurrency = "USD",
            ImputedAmount = 400m,
            ExchangeRate = 1000m,
            ExchangeRateSource = ExchangeRateSource.BCRA_A3500,
            ExchangeRateAt = DateTime.UtcNow.AddDays(-3),
            PaidAt = DateTime.UtcNow.AddDays(-3),
            Method = "Transfer",
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true,
            RelatedInvoiceId = 800,
        });
        await ctx.SaveChangesAsync();

        var service = BuildAfipService(ctx);
        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var reversal = await ctx.Payments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal);
        // Cap = USD 400 imputados (NO los $400.000 ARS de caja real, ni los USD 1000 de la NC).
        Assert.Equal(-400m, reversal!.Amount);
        Assert.Equal("USD", reversal.Currency);
    }

    // =========================================================================
    // Escenario 4: dos NC parciales sucesivas sobre la MISMA factura. La 2da solo puede revertir
    // lo que la 1ra todavia no revirtio (si no restamos las reversiones previas, se duplica el
    // descuento y aparece un saldo a favor fantasma -> el bug simetrico).
    // =========================================================================

    [Fact]
    public async Task DosNcParcialesSucesivas_LaSegundaSoloRevierteLoQueQuedaSinRevertir()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        // Factura original de $1000, cobrada por completo.
        SeedReservaYFacturaOriginal(ctx, originalAmount: 1000m);
        ctx.Payments.Add(new Payment
        {
            Id = 500,
            ReservaId = 1,
            Amount = 1000m,
            Currency = "ARS",
            PaidAt = DateTime.UtcNow.AddDays(-3),
            Method = "Transfer",
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true,
            RelatedInvoiceId = 800,
        });

        var customer = new Customer { Id = 1, FullName = "Cliente T0", TaxCondition = "Consumidor Final", IsActive = true };
        var supplier = new Supplier
        {
            Id = 1, Name = "Operador T0", IsActive = true,
            TaxCondition = "IVA_RESP_INSCRIPTO", InvoicingMode = SupplierInvoicingMode.TotalToCustomer,
        };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);

        // Primer NC parcial: $600.
        var nc1 = BuildCreditNoteInvoice(id: 801, ncAmount: 600m);
        ctx.Invoices.Add(nc1);
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            ReservaId = 1,
            CustomerId = 1,
            SupplierId = 1,
            OriginatingInvoiceId = 800,
            CreditNoteInvoiceId = 801,
            Status = BookingCancellationStatus.AwaitingFiscalConfirmation,
            Reason = "Cancelacion parcial 1",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            CreditNoteKind = CreditNoteKind.PartialOnOriginal,
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.BCRA_A3500,
                ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS",
                FetchedAt = DateTime.UtcNow,
            },
        });
        await ctx.SaveChangesAsync();

        var service = BuildAfipService(ctx);
        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var reversal1 = await ctx.Payments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal && p.RelatedInvoiceId == 801);
        Assert.NotNull(reversal1);
        Assert.Equal(-600m, reversal1!.Amount);

        // Segunda NC parcial: otros $600 (pero solo quedan $400 de los $1000 originales sin revertir).
        var nc2 = BuildCreditNoteInvoice(id: 802, ncAmount: 600m);
        ctx.Invoices.Add(nc2);
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            Id = 2,
            PublicId = Guid.NewGuid(),
            ReservaId = 1,
            CustomerId = 1,
            SupplierId = 1,
            OriginatingInvoiceId = 800,
            CreditNoteInvoiceId = 802,
            Status = BookingCancellationStatus.AwaitingFiscalConfirmation,
            Reason = "Cancelacion parcial 2",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            CreditNoteKind = CreditNoteKind.PartialOnOriginal,
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.BCRA_A3500,
                ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS",
                FetchedAt = DateTime.UtcNow,
            },
        });
        await ctx.SaveChangesAsync();

        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 802);

        var reversal2 = await ctx.Payments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal && p.RelatedInvoiceId == 802);
        Assert.NotNull(reversal2);
        // Disponible real = 1000 cobrado - 600 ya revertido = 400. La NC pide 600 pero solo
        // quedan 400 sin revertir -> el cap topea ahi (NO -600, que duplicaria el descuento).
        Assert.Equal(-400m, reversal2!.Amount);
    }

    // =========================================================================
    // Escenario 5 (regresion): factura totalmente cobrada -> NC total -> reversal completo con
    // la Currency correcta. Confirma que el fix NO rompe el caso feliz historico.
    // =========================================================================

    [Fact]
    public async Task NcTotal_FacturaTotalmenteCobrada_ReversalCompletoConMonedaCorrecta()
    {
        await using var ctx = new AppDbContext(_dbOptions);

        SeedReservaYFacturaOriginal(ctx, originalAmount: 1000m, originalMonId: "DOL");
        ctx.Invoices.Add(BuildCreditNoteInvoice(id: 801, ncAmount: 1000m, ncMonId: "DOL"));
        ctx.Payments.Add(new Payment
        {
            Id = 500,
            ReservaId = 1,
            Amount = 1000m,
            Currency = "USD",
            PaidAt = DateTime.UtcNow.AddDays(-3),
            Method = "Transfer",
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true,
            RelatedInvoiceId = 800,
        });
        await ctx.SaveChangesAsync();

        var service = BuildAfipService(ctx);
        await service.ApplyCreditNoteEconomicReversalAsync(invoiceId: 801);

        var reversal = await ctx.Payments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal);
        Assert.Equal(-1000m, reversal!.Amount);
        Assert.Equal("USD", reversal.Currency);
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
