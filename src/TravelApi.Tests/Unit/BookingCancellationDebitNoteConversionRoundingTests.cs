using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 T3b Decision 2 — invariante de redondeo (2026-08-05): <c>BuildCancellationDebitNoteItemsAsync</c> arma
/// un renglon de Nota de Debito POR CADA cargo confirmado del operador, convirtiendo a la moneda de la factura
/// destino cuando hace falta (<c>ConvertArsUsdAmount</c>). A diferencia de
/// <c>AllocateConfirmedPenaltyToLinesAsync</c> (que reparte un ÚNICO monto entre varios renglones, y ahí sí hacia
/// falta un absorbedor de residuo — ver <see cref="BookingCancellationPenaltyAllocationRoundingTests"/>), esta
/// otra funcion NO reparte nada: cada renglon es un cargo YA determinado, y el total de la ND es simplemente la
/// SUMA de esos renglones (se acumula sobre la marcha, <c>total += amountInInvoiceCurrency</c>). Por construccion
/// no puede haber fuga de centavos aca — no hay un total previo del que "descontar" nada.
///
/// <para>Este test fija esa invariante con un caso de conversion con TC de 6 decimales que deja resto: si en el
/// futuro alguien reescribe el metodo para calcular el total de otra forma (por ejemplo recotizando el total en
/// vez de sumar los renglones ya convertidos), este test se rompe y avisa.</para>
/// </summary>
public class BookingCancellationDebitNoteConversionRoundingTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bc-nd-conversion-rounding-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record Harness(BookingCancellationService Service, AppDbContext Ctx, CreateInvoiceRequest? CapturedRequest);

    /// <summary>Mismo harness que Adr044T3bTargetInvoiceAndTreasuryFxTests, pero capturando el <see cref="CreateInvoiceRequest"/> completo (no solo su total) para poder revisar los renglones uno por uno.</summary>
    private static (BookingCancellationService Service, AppDbContext Ctx, Func<CreateInvoiceRequest?> GetCapturedRequest) BuildService()
    {
        var ctx = NewDbContext();
        CreateInvoiceRequest? captured = null;

        var invoiceMock = new Mock<IInvoiceService>();
        invoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, System.Threading.CancellationToken ct) =>
            {
                captured = req;
                var nd = new Invoice
                {
                    PublicId = Guid.NewGuid(), TipoComprobante = 12, Resultado = "A",
                    ImporteTotal = req.Items.Sum(i => i.Total), MonId = req.MonId, MonCotiz = req.MonCotiz,
                };
                ctx.Invoices.Add(nd);
                ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(
            new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true, EnableCancellationDebitNote = true,
                EnableMultiCurrencyInvoicing = true, CancellationDebitNoteGraceDays = 15,
                CancellationDebitNoteHardWarnDays = 60, CancellationDebitNoteFourEyesThreshold = 2_000_000m,
            });

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

        return (service, ctx, () => captured);
    }

    private static async Task<(BookingCancellation Bc, Reserva Reserva)> SeedConfirmedBcWithArsInvoiceAsync(
        AppDbContext ctx, Supplier supplier)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-CONV", Name = "Reserva Test", PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 900, CAE = "cae-orig",
            Resultado = "A", MonId = "PES", ImporteTotal = 500_000m, ImporteNeto = 500_000m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 901, CAE = "cae-nc",
            Resultado = "A", ReservaId = reserva.Id,
        };
        ctx.Invoices.Add(original);
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();
        creditNote.OriginalInvoiceId = original.Id;
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion test", DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-5), ConfirmedByUserId = "vendedor-1",
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            PenaltyStatus = PenaltyStatus.Confirmed,
            ConceptClassifiedByUserId = "u1", ConceptClassifiedByUserName = "U1",
            ConceptClassifiedAt = DateTime.UtcNow.AddDays(-1),
            PenaltyConfirmedByUserId = "u1", PenaltyConfirmedByUserName = "U1",
            PenaltyConfirmedAt = DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge,
            PenaltyAmountAtEvent = 1m, PenaltyCurrencyAtEvent = "ARS",
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = "ARS", AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent = "MONOTRIBUTISTA", CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
                Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m,
                FetchedAt = DateTime.UtcNow.AddDays(-5),
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc, reserva);
    }

    private static async Task<BookingCancellationLine> AddConfirmedLineWithUsdChargeAsync(
        AppDbContext ctx, BookingCancellation bc, Supplier supplier, decimal amount, decimal estimatedRate)
    {
        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = ctx.BookingCancellationLines.Count() + 1,
            Scope = BookingCancellationLineScope.Full, Currency = "DOL",
            RefundCap = 0m, PenaltyAmount = amount, RetainedDeductionAmount = amount,
            PenaltyStatus = PenaltyStatus.Confirmed,
        };
        ctx.BookingCancellationLines.Add(line);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = line.Id, Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = PenaltyCollectionMode.Retenida, Amount = amount, Currency = "DOL",
            ClientTransferMode = ClientTransferMode.AsIs, ConfirmedByUserId = "u1", ConfirmedByUserName = "U1",
            ConfirmedAt = DateTime.UtcNow.AddDays(-1),
            EstimatedExchangeRateToClientInvoiceCurrency = estimatedRate,
            EstimatedExchangeRateSource = ExchangeRateSource.Manual,
            EstimatedExchangeRateAt = DateTime.UtcNow.AddDays(-1),
            EstimatedExchangeRateJustification = "TC manual cargado al confirmar.",
        });
        await ctx.SaveChangesAsync();

        return line;
    }

    /// <summary>
    /// Dos cargos en USD (el operador cobro la multa en dolares), convertidos a la factura del cliente en ARS
    /// con el MISMO tipo de cambio de 6 decimales que deja resto al redondear cada renglon por separado. Con UNA
    /// sola factura activa (la originante), no hace falta resolver TargetInvoiceId entre varias.
    /// </summary>
    [Fact]
    public async Task Emit_DosCargosCrossCurrency_TotalDeLaNDEsLaSumaExactaDeLosRenglonesYaConvertidos()
    {
        var (service, ctx, getCapturedRequest) = BuildService();
        var supplier = new Supplier { Name = "Operador Test", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var (bc, _) = await SeedConfirmedBcWithArsInvoiceAsync(ctx, supplier);
        var originatingInvoice = await ctx.Invoices.SingleAsync(i => i.Id == bc.OriginatingInvoiceId);

        const decimal tcSeisDecimales = 350.654321m;
        await AddConfirmedLineWithUsdChargeAsync(ctx, bc, supplier, amount: 33.33m, estimatedRate: tcSeisDecimales);
        await AddConfirmedLineWithUsdChargeAsync(ctx, bc, supplier, amount: 66.67m, estimatedRate: tcSeisDecimales);

        var dto = await service.RetryDebitNoteEmissionAsync(
            bc.PublicId, "u", "U", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("Pending", dto.DebitNoteStatus);
        var request = getCapturedRequest();
        Assert.NotNull(request);

        // Cada renglon se convierte y redondea INDEPENDIENTE (asi funciona una factura real: cada linea es un
        // numero cerrado a 2 decimales). El total de la ND tiene que ser la suma EXACTA de esos renglones ya
        // redondeados — nunca una recotizacion del total en bloque, que daria un numero distinto.
        var esperadoRenglon1 = Math.Round(33.33m * tcSeisDecimales, 2, MidpointRounding.AwayFromZero);
        var esperadoRenglon2 = Math.Round(66.67m * tcSeisDecimales, 2, MidpointRounding.AwayFromZero);
        var totalEsperado = esperadoRenglon1 + esperadoRenglon2;

        Assert.Equal(2, request!.Items.Count);
        Assert.Equal(totalEsperado, request.Items.Sum(i => i.Total));

        var ndPersistida = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.Id != originatingInvoice.Id && i.TipoComprobante == 12);
        Assert.Equal(totalEsperado, ndPersistida.ImporteTotal);
    }
}
