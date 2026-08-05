using System;
using System.Collections.Generic;
using System.Linq;
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
/// Fix redondeo con tope (2026-08-05): <c>AllocateConfirmedPenaltyToLinesAsync</c> reparte la multa confirmada
/// del operador entre TODOS los renglones (<see cref="BookingCancellationLine"/>) de ESE operador, en proporcion
/// a lo que cada uno tiene pendiente de devolver (<c>RefundCap</c>). El reparto viejo (una sola pasada) fallaba
/// en DOS direcciones: podia FALTAR plata sin asignar (si el redondeo de renglones intermedios le dejaba al
/// ULTIMO un residuo mas grande que su propio cap, el clamp de seguridad lo recortaba y esa plata quedaba
/// perdida en silencio) o podia SOBRAR (con caps EMPATADOS y una multa chica, cada renglon redondea su porcion
/// de forma independiente y la suma de esos redondeos puede superar la multa entera — hallazgo del review,
/// contraejemplo: 4 renglones de $0.01 y multa $0.02). El fix agrega un PASE de recorte (si sobro) y un PASE de
/// relleno (si falto), asi la suma de los renglones cuadra SIEMPRE centavo a centavo contra la multa aplicada.
///
/// <para>Estos tests son de INVARIANTE: para cualquier combinacion de caps, <c>suma(PenaltyAmount de cada
/// renglon) == penaltyToApply</c> (el monto de multa efectivamente neteado, nunca mayor a lo que el operador
/// tenia para devolver). Mismo harness que <see cref="Adr044T2OperatorChargeTests"/>.</para>
/// </summary>
public class BookingCancellationPenaltyAllocationRoundingTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bc-penalty-rounding-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record Harness(BookingCancellationService Service, AppDbContext Ctx);

    private static Harness BuildService()
    {
        var ctx = NewDbContext();
        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnableCancellationDebitNote = true,
            EnableMultiCurrencyInvoicing = true,
            CancellationDebitNoteGraceDays = 15,
            CancellationDebitNoteHardWarnDays = 60,
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
        };
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(settings);

        var service = new BookingCancellationService(
            ctx,
            new Mock<IInvoiceService>().Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

        return new Harness(service, ctx);
    }

    /// <summary>
    /// Siembra un BC con VARIOS renglones del MISMO operador, cada uno con el RefundCap indicado en
    /// <paramref name="refundCaps"/> (todos en la misma moneda). Devuelve el BC (tracked) para pasarlo a
    /// <c>AllocateConfirmedPenaltyToLinesAsync</c>.
    /// </summary>
    private static async Task<BookingCancellation> SeedBcWithLinesAsync(
        AppDbContext ctx, IReadOnlyList<decimal> refundCaps, string currency = "ARS")
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador X", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-RND", Name = "Reserva Test", PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 100, CAE = "12345678",
            Resultado = "A", MonId = "PES", ImporteTotal = 100_000m, ImporteNeto = 100_000m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 101, CAE = "99999999",
            Resultado = "A", ReservaId = reserva.Id, OriginalInvoiceId = original.Id,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cliente anulo", DraftedByUserId = "vendedor-1",
            ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-10),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        foreach (var cap in refundCaps)
        {
            ctx.BookingCancellationLines.Add(new BookingCancellationLine
            {
                BookingCancellationId = bc.Id, SupplierId = supplier.Id, Currency = currency, RefundCap = cap,
            });
        }
        await ctx.SaveChangesAsync();

        return bc;
    }

    /// <summary>
    /// Reproduce el bug original: dos renglones con caps MUY dispares (10.00 y 0.01) y una multa que se come
    /// TODO lo pagado (penaltyToApply == la suma de los caps). El reparto proporcional viejo le daba al primer
    /// renglon 9.99 (redondeo hacia abajo) y al ultimo un residuo de 0.02 que su propio cap (0.01) no podia
    /// absorber -> quedaba 1 centavo sin asignar a NADIE. Con el fix, cada renglon termina con exactamente SU
    /// propio cap (no queda nada pendiente de reembolso: el operador se quedo con todo).
    /// </summary>
    [Fact]
    public async Task Allocate_CapsMuyDispares_MultaConsumeTodo_NoPierdeCentavos()
    {
        var h = BuildService();
        var bc = await SeedBcWithLinesAsync(h.Ctx, new[] { 10.00m, 0.01m });

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, confirmedPenaltyAmount: 10.01m, "ARS", default);
        await h.Ctx.SaveChangesAsync();

        var lines = await h.Ctx.BookingCancellationLines.AsNoTracking().ToListAsync();
        Assert.Equal(10.01m, lines.Sum(l => l.PenaltyAmount ?? 0m));
        // Cada renglon absorbio EXACTO su propio cap: no queda reembolso pendiente en ninguno.
        Assert.All(lines, l => Assert.Equal(0m, l.RefundCap));

        var charges = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().ToListAsync();
        Assert.Equal(10.01m, charges.Sum(c => c.Amount));
    }

    /// <summary>
    /// Mismo bug con TRES renglones de centavos dispares que juntos consumen toda la multa — el caso explicito
    /// que pidio el brief ("3 renglones de $0,01 de diferencia").
    /// </summary>
    [Fact]
    public async Task Allocate_TresRenglonesConCentavosDispares_MultaConsumeTodo_SumaExacta()
    {
        var h = BuildService();
        var bc = await SeedBcWithLinesAsync(h.Ctx, new[] { 0.01m, 0.01m, 33.32m });
        const decimal penaltyTotal = 0.01m + 0.01m + 33.32m; // 33.34

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, confirmedPenaltyAmount: penaltyTotal, "ARS", default);
        await h.Ctx.SaveChangesAsync();

        var lines = await h.Ctx.BookingCancellationLines.AsNoTracking().ToListAsync();
        Assert.Equal(penaltyTotal, lines.Sum(l => l.PenaltyAmount ?? 0m));
        Assert.All(lines, l => Assert.Equal(0m, l.RefundCap));
    }

    /// <summary>
    /// Prorrateo clasico en tercios SIN agotar el cap (el operador no se quedo con todo, solo con una parte):
    /// 3 renglones de $100 cada uno (cap total $300), multa confirmada de $100 -> a cada renglon le toca
    /// 100/3 = 33.333... Sigue valiendo la invariante de siempre: la suma de las porciones es EXACTA, sin que
    /// se activen ni el pase de recorte ni el de relleno (ningun renglon llega a su tope).
    /// </summary>
    [Fact]
    public async Task Allocate_ProrrateoEnTercios_SinAgotarCap_SumaExacta()
    {
        var h = BuildService();
        var bc = await SeedBcWithLinesAsync(h.Ctx, new[] { 100m, 100m, 100m });

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, confirmedPenaltyAmount: 100m, "ARS", default);
        await h.Ctx.SaveChangesAsync();

        var lines = await h.Ctx.BookingCancellationLines.AsNoTracking().ToListAsync();
        Assert.Equal(100m, lines.Sum(l => l.PenaltyAmount ?? 0m));
        // Ningun renglon absorbio mas de lo que le correspondia proporcionalmente (100/3 redondeado).
        Assert.All(lines, l => Assert.True(l.PenaltyAmount is >= 33.33m and <= 33.34m));
    }

    /// <summary>
    /// Contraejemplo EXACTO del review (F-12, 2026-08-05): 4 renglones con el MISMO cap chico ($0.01 c/u) y una
    /// multa tambien chica ($0.02, la mitad del cap total $0.04). El pase 1, redondeando cada renglon
    /// independiente con <c>AwayFromZero</c>, le da $0.01 a cada uno de los primeros 3 renglones (redondea
    /// "para afuera" el empate de 0.005) — eso solo ya suma $0.03, MAS que la multa entera de $0.02. Sin el
    /// pase de recorte, el sistema le habria retenido al operador $0.03 cuando la multa real era $0.02.
    /// </summary>
    [Fact]
    public async Task Allocate_CapsEmpatadosMultaChica_ElPase1SumaDeMas_SeRecortaAlExacto()
    {
        var h = BuildService();
        var bc = await SeedBcWithLinesAsync(h.Ctx, new[] { 0.01m, 0.01m, 0.01m, 0.01m });

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, confirmedPenaltyAmount: 0.02m, "ARS", default);
        await h.Ctx.SaveChangesAsync();

        var lines = await h.Ctx.BookingCancellationLines.AsNoTracking().ToListAsync();
        // Invariante central del fix F-12: la suma nunca supera (ni queda por debajo de) la multa aplicada.
        Assert.Equal(0.02m, lines.Sum(l => l.PenaltyAmount ?? 0m));
        // El total de reembolso pendiente que queda es exactamente lo que la multa NO se llevo.
        Assert.Equal(0.02m, lines.Sum(l => l.RefundCap));
        Assert.All(lines, l => Assert.True((l.PenaltyAmount ?? 0m) >= 0m));
        Assert.All(lines, l => Assert.True(l.RefundCap >= 0m));

        var charges = await h.Ctx.BookingCancellationLineOperatorCharges.AsNoTracking().ToListAsync();
        // El eje CAJA (los cargos que se crean por detras) tiene que coincidir con el eje CLIENTE: no se le
        // cobra al operador un cargo por mas de lo que la multa realmente aplico.
        Assert.Equal(0.02m, charges.Sum(c => c.Amount));
    }

    /// <summary>
    /// Multa que NO llega a consumir todo el cap disponible, con caps dispares (mismo escenario adversarial que
    /// el primer test, pero con margen): confirma que el pase de relleno no se activa de mas ni rompe el caso
    /// sano.
    /// </summary>
    [Fact]
    public async Task Allocate_CapsDispares_MultaParcial_SumaExactaYRespetaCadaTope()
    {
        var h = BuildService();
        var originalCaps = new[] { 10.00m, 0.01m };
        var bc = await SeedBcWithLinesAsync(h.Ctx, originalCaps);

        await h.Service.AllocateConfirmedPenaltyToLinesAsync(bc, confirmedPenaltyAmount: 10.00m, "ARS", default);
        await h.Ctx.SaveChangesAsync();

        var lines = await h.Ctx.BookingCancellationLines.AsNoTracking().ToListAsync();
        // La multa aplicada cuadra exacto contra lo que efectivamente se neteo.
        Assert.Equal(10.00m, lines.Sum(l => l.PenaltyAmount ?? 0m));
        // Del cap original (10.01) queda sin devolver justo lo que la multa se llevo (10.00): sobra 0.01 de
        // reembolso pendiente, repartido en algun renglon — nunca se inventa ni se pierde plata.
        Assert.Equal(0.01m, lines.Sum(l => l.RefundCap));
        // Invariante B1 de siempre, ahora chequeada renglon por renglon: RefundCap + PenaltyAmount == cap original.
        Assert.Equal(originalCaps.Sum(), lines.Sum(l => l.RefundCap + (l.PenaltyAmount ?? 0m)));
        Assert.All(lines, l => Assert.True(l.RefundCap >= 0m));
        Assert.All(lines, l => Assert.True((l.PenaltyAmount ?? 0m) >= 0m));
    }
}
