using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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
/// FASE 0 (2026-06-28): confirmar la penalidad del operador debe BAJAR el reembolso esperado de ese operador por el
/// monto de la multa. Antes, confirmar la multa solo escribia el escalar del BC padre (para la ND al cliente) y el
/// read-model "Reembolsos a cobrar" (RefundCap − recibido) seguia SOBREESTIMANDO. Estos tests prueban que tras
/// confirmar una multa X en la moneda C, el reembolso esperado del operador en C = pagado − X − ya recibido, que las
/// monedas quedan SEPARADAS (nunca cruzado ARS/USD), que el caso sin multa no cambia nada, y que la cara fiscal del
/// cliente (<c>bc.PenaltyAmountAtEvent</c>) NO se toca. ADR-044 T1 (2026-07-10):
/// <c>line.PenaltyStatus</c> SI se marca <c>Confirmed</c> desde esta tanda (ver
/// <see cref="ConfirmPenalty_marksLinePenaltyStatusConfirmed"/>) — es lo que activa el candado multi-operador de
/// la ND (<c>CountSuppliersWithConfirmedPenaltyAsync</c>), que antes nunca disparaba de verdad.
///
/// <para>Tests UNIT con EF InMemory (sin Docker). Ejercitan <c>AllocateConfirmedPenaltyToLinesAsync</c> (internal)
/// directamente y verifican el numero end-to-end con <see cref="OperatorRefundReadModelService"/>. InMemory NO valida
/// CHECK/xmin; cubrimos la LOGICA del reparto y del read-model.</para>
/// </summary>
public class OperatorRefundPenaltyNetsRefundCapTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"penalty-nets-cap-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildService(AppDbContext ctx)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });

        return new BookingCancellationService(
            ctx,
            new Mock<IInvoiceService>().Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    private static IHttpContextAccessor AdminAccessor()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Role, "Admin") }, authenticationType: "test")),
        };
        return new HttpContextAccessor { HttpContext = http };
    }

    /// <summary>
    /// Siembra un BC AwaitingOperatorRefund cuyo operador PRINCIPAL es <paramref name="principalSupplier"/>, con su
    /// factura de origen (MapToDtoAsync y el read-model la requieren). Devuelve el BC trackeado. Las lineas se
    /// agregan aparte con <see cref="AddLine"/>.
    /// </summary>
    private static async Task<(BookingCancellation Bc, Supplier SupplierA, Supplier SupplierB)> SeedBcAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente Fase0", IsActive = true };
        var supplierA = new Supplier { Name = "Operador Principal A", IsActive = true };
        var supplierB = new Supplier { Name = "Operador Secundario B", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplierA);
        ctx.Suppliers.Add(supplierB);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-F0", Name = "Reserva Fase 0", PayerId = customer.Id, Status = EstadoReserva.Cancelled };
        ctx.Reservas.Add(reserva);
        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplierA.Id, // operador PRINCIPAL del evento
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "cancelacion con refund esperado",
            DraftedByUserId = "v",
            OperatorRefundDueBy = DateTime.UtcNow.AddDays(30),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc, supplierA, supplierB);
    }

    private static void AddLine(AppDbContext ctx, int bcId, int supplierId, decimal cap, decimal received = 0m, string currency = "ARS")
    {
        ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
        {
            BookingCancellationId = bcId,
            SupplierId = supplierId,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Full,
            Currency = currency,
            LineSaleAmount = cap,
            RefundCap = cap, // al draft, RefundCap == capBeforePenalty (la multa todavia es null)
            ReceivedRefundAmount = received,
        });
    }

    // =====================================================================================
    // Caso central: confirmar multa X en C -> reembolso esperado = pagado − X − recibido
    // =====================================================================================

    [Fact]
    public async Task ConfirmPenalty_reducesExpectedRefund_byPenalty_singleCurrency()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        AddLine(ctx, bc.Id, supplierA.Id, cap: 1000m, received: 0m, currency: "ARS");
        await ctx.SaveChangesAsync();

        await service.AllocateConfirmedPenaltyToLinesAsync(bc, confirmedPenaltyAmount: 300m, requestedPenaltyCurrency: "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var line = await ctx.Set<BookingCancellationLine>().AsNoTracking().FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(700m, line.RefundCap);      // 1000 − 300
        Assert.Equal(300m, line.PenaltyAmount);

        // End-to-end: el read-model muestra pagado − multa − recibido = 700.
        var readModel = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var items = await readModel.GetSupplierPendingRefundsAsync(supplierA.Id, CancellationToken.None);
        var estimated = items.Single().EstimatedRefundsByCurrency.Single();
        Assert.Equal("ARS", estimated.Currency);
        Assert.Equal(700m, estimated.EstimatedAmount);
    }

    [Fact]
    public async Task ConfirmPenalty_reducesExpectedRefund_accountingForAlreadyReceived()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        // Pagado 1000, ya recibido 100 -> antes del fix el read-model mostraba 900 (sobreestimado: ignoraba la multa).
        AddLine(ctx, bc.Id, supplierA.Id, cap: 1000m, received: 100m, currency: "ARS");
        await ctx.SaveChangesAsync();

        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var readModel = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var items = await readModel.GetSupplierPendingRefundsAsync(supplierA.Id, CancellationToken.None);
        // pagado(1000) − multa(300) − recibido(100) = 600.
        Assert.Equal(600m, items.Single().EstimatedRefundsByCurrency.Single().EstimatedAmount);
    }

    // =====================================================================================
    // Multimoneda: las monedas quedan SEPARADAS, nunca se netea cruzado
    // =====================================================================================

    [Fact]
    public async Task ConfirmPenalty_inUsd_onlyReducesUsdLines_arsUntouched()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        AddLine(ctx, bc.Id, supplierA.Id, cap: 1000m, currency: "ARS");
        AddLine(ctx, bc.Id, supplierA.Id, cap: 500m, currency: "USD");
        await ctx.SaveChangesAsync();

        // Multa de 300 en USD: solo la linea USD baja; la ARS queda intacta.
        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "USD", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var lines = await ctx.Set<BookingCancellationLine>().AsNoTracking().Where(l => l.BookingCancellationId == bc.Id).ToListAsync();
        var arsLine = lines.Single(l => l.Currency == "ARS");
        var usdLine = lines.Single(l => l.Currency == "USD");
        Assert.Equal(1000m, arsLine.RefundCap);   // ARS NO se toca
        Assert.Null(arsLine.PenaltyAmount);
        Assert.Equal(200m, usdLine.RefundCap);     // 500 − 300
        Assert.Equal(300m, usdLine.PenaltyAmount);

        var readModel = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var item = (await readModel.GetSupplierPendingRefundsAsync(supplierA.Id, CancellationToken.None)).Single();
        Assert.Equal(1000m, item.EstimatedRefundsByCurrency.Single(e => e.Currency == "ARS").EstimatedAmount);
        Assert.Equal(200m, item.EstimatedRefundsByCurrency.Single(e => e.Currency == "USD").EstimatedAmount);
    }

    [Fact]
    public async Task ConfirmPenalty_multiCurrencyOperator_withoutExplicitCurrency_isNoOp_avoidsCrossNetting()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        AddLine(ctx, bc.Id, supplierA.Id, cap: 1000m, currency: "ARS");
        AddLine(ctx, bc.Id, supplierA.Id, cap: 500m, currency: "USD");
        await ctx.SaveChangesAsync();

        // Sin PenaltyCurrency y con servicios en 2 monedas: no se puede elegir moneda -> NO se netea (anti cross-currency).
        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, requestedPenaltyCurrency: null, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var lines = await ctx.Set<BookingCancellationLine>().AsNoTracking().Where(l => l.BookingCancellationId == bc.Id).ToListAsync();
        Assert.All(lines, l => Assert.Null(l.PenaltyAmount));        // ninguna linea se neteo
        Assert.Equal(1000m, lines.Single(l => l.Currency == "ARS").RefundCap);
        Assert.Equal(500m, lines.Single(l => l.Currency == "USD").RefundCap);
    }

    // =====================================================================================
    // Multi-operador: solo el operador PRINCIPAL baja; el otro conserva su reembolso
    // =====================================================================================

    [Fact]
    public async Task ConfirmPenalty_multiOperator_onlyPrincipalOperatorReduced()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, supplierB) = await SeedBcAsync(ctx); // bc.SupplierId == A (principal)
        AddLine(ctx, bc.Id, supplierA.Id, cap: 1000m, currency: "ARS");
        AddLine(ctx, bc.Id, supplierB.Id, cap: 800m, currency: "ARS");
        await ctx.SaveChangesAsync();

        // Multa 300 ARS (sin PenaltyCurrency: el operador principal tiene una sola moneda -> se infiere ARS).
        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, requestedPenaltyCurrency: null, CancellationToken.None);
        await ctx.SaveChangesAsync();

        var lines = await ctx.Set<BookingCancellationLine>().AsNoTracking().Where(l => l.BookingCancellationId == bc.Id).ToListAsync();
        Assert.Equal(700m, lines.Single(l => l.SupplierId == supplierA.Id).RefundCap); // principal baja
        Assert.Equal(300m, lines.Single(l => l.SupplierId == supplierA.Id).PenaltyAmount);
        Assert.Equal(800m, lines.Single(l => l.SupplierId == supplierB.Id).RefundCap); // secundario intacto
        Assert.Null(lines.Single(l => l.SupplierId == supplierB.Id).PenaltyAmount);

        // El read-model global muestra cada operador con su propio numero (A neto de multa, B completo).
        var readModel = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var all = await readModel.GetAllPendingRefundsAsync(CancellationToken.None);
        Assert.Equal(700m, all.Single(i => i.SupplierName == supplierA.Name).EstimatedRefundsByCurrency.Single().EstimatedAmount);
        Assert.Equal(800m, all.Single(i => i.SupplierName == supplierB.Name).EstimatedRefundsByCurrency.Single().EstimatedAmount);
    }

    // =====================================================================================
    // Reparto proporcional entre varias lineas del mismo operador/moneda
    // =====================================================================================

    [Fact]
    public async Task ConfirmPenalty_distributesProportionally_acrossLines_sumExact()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        AddLine(ctx, bc.Id, supplierA.Id, cap: 600m, currency: "ARS");
        AddLine(ctx, bc.Id, supplierA.Id, cap: 400m, currency: "ARS");
        await ctx.SaveChangesAsync();

        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 250m, "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var lines = await ctx.Set<BookingCancellationLine>().AsNoTracking().Where(l => l.BookingCancellationId == bc.Id).OrderByDescending(l => l.LineSaleAmount).ToListAsync();
        // 250 * 600/1000 = 150 ; el residuo (100) cae en la ultima -> 600-150=450 y 400-100=300.
        Assert.Equal(150m, lines[0].PenaltyAmount);
        Assert.Equal(450m, lines[0].RefundCap);
        Assert.Equal(100m, lines[1].PenaltyAmount);
        Assert.Equal(300m, lines[1].RefundCap);
        // La suma de las multas imputadas == la multa confirmada (sin perder ni inventar centavos).
        Assert.Equal(250m, lines.Sum(l => l.PenaltyAmount ?? 0m));

        var readModel = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var item = (await readModel.GetSupplierPendingRefundsAsync(supplierA.Id, CancellationToken.None)).Single();
        Assert.Equal(750m, item.EstimatedRefundsByCurrency.Single().EstimatedAmount); // 1000 − 250
    }

    // =====================================================================================
    // Multa > pagado: el reembolso cae a 0 (no negativo) y la ND al cliente NO se toca
    // =====================================================================================

    [Fact]
    public async Task ConfirmPenalty_largerThanPaid_floorsRefundAtZero_doesNotTouchCustomerNdAmount()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        AddLine(ctx, bc.Id, supplierA.Id, cap: 1000m, currency: "ARS");
        await ctx.SaveChangesAsync();

        // El escalar del padre (lo que alimenta la ND al CLIENTE) ya viene seteado: NO debe cambiarlo la imputacion.
        bc.PenaltyAmountAtEvent = 1500m;
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        await ctx.SaveChangesAsync();

        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 1500m, "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var line = await ctx.Set<BookingCancellationLine>().AsNoTracking().FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(0m, line.RefundCap);             // no puede devolver menos que cero
        Assert.Equal(1000m, line.PenaltyAmount);      // se imputa solo hasta lo pagado (preserva la invariante)
        // La invariante RefundCap + PenaltyAmount == capBeforePenalty (1000) se mantiene.
        Assert.Equal(1000m, line.RefundCap + (line.PenaltyAmount ?? 0m));

        // La cara fiscal del cliente NO se toca: el monto COMPLETO de la ND sigue en el escalar del padre.
        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(1500m, bcAfter.PenaltyAmountAtEvent);

        var readModel = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var item = (await readModel.GetSupplierPendingRefundsAsync(supplierA.Id, CancellationToken.None)).Single();
        Assert.Equal(0m, item.EstimatedRefundsByCurrency.Single().EstimatedAmount);
    }

    // =====================================================================================
    // Sin multa / cero: no cambia nada. Y el estado fiscal de la linea queda intacto.
    // =====================================================================================

    [Fact]
    public async Task ConfirmPenalty_zeroAmount_isNoOp()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        AddLine(ctx, bc.Id, supplierA.Id, cap: 1000m, currency: "ARS");
        await ctx.SaveChangesAsync();

        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 0m, "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var line = await ctx.Set<BookingCancellationLine>().AsNoTracking().FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(1000m, line.RefundCap);
        Assert.Null(line.PenaltyAmount);
    }

    [Fact]
    public async Task ConfirmPenalty_marksLinePenaltyStatusConfirmed()
    {
        // ADR-044 T1 (2026-07-10): DESDE esta tanda, imputar la multa a las lineas de un operador TAMBIEN marca su
        // PenaltyStatus=Confirmed (antes de esta tanda, a proposito, NO lo hacia: era el bug M2 del rediseño de
        // multas — CountSuppliersWithConfirmedPenaltyAsync cuenta lineas con PenaltyStatus=Confirmed, y como nada
        // lo seteaba, el candado multi-operador de la ND nunca disparaba de verdad). Este test reemplaza al viejo
        // "ConfirmPenalty_doesNotMarkLinePenaltyStatusConfirmed", que fijaba el comportamiento OPUESTO al que esta
        // tanda corrige a proposito.
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        AddLine(ctx, bc.Id, supplierA.Id, cap: 1000m, currency: "ARS");
        await ctx.SaveChangesAsync();

        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var line = await ctx.Set<BookingCancellationLine>().AsNoTracking().FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(PenaltyStatus.Confirmed, line.PenaltyStatus);
    }

    [Fact]
    public async Task ConfirmPenalty_calledTwice_isIdempotentNoOp_invariantHolds()
    {
        // Hardening: aunque las guardas externas (Precondicion 6 = 409, transicion de una via, xmin) impiden una
        // segunda llamada, el metodo debe ser seguro por si solo. La 2da invocacion NO debe volver a restar la multa.
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        AddLine(ctx, bc.Id, supplierA.Id, cap: 1000m, currency: "ARS");
        await ctx.SaveChangesAsync();

        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        // 2da llamada: debe ser no-op (la linea ya tiene PenaltyAmount cargado).
        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var lineAfter = await ctx.Set<BookingCancellationLine>().AsNoTracking().FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(700m, lineAfter.RefundCap);     // sin doble resta (seria 400 si no fuera idempotente)
        Assert.Equal(300m, lineAfter.PenaltyAmount);
        Assert.Equal(1000m, lineAfter.RefundCap + (lineAfter.PenaltyAmount ?? 0m)); // invariante intacta
    }

    [Fact]
    public async Task ConfirmPenalty_operatorNotPaid_zeroCap_doesNotSetPenaltyAmount()
    {
        // Si al operador no se le pago (cap 0), no hay reembolso que bajar; no seteamos PenaltyAmount para no romper
        // la invariante RefundCap + PenaltyAmount == capBeforePenalty (0 + 0 == 0) que usa AssignRefundCaps.
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, supplierA, _) = await SeedBcAsync(ctx);
        AddLine(ctx, bc.Id, supplierA.Id, cap: 0m, currency: "ARS");
        await ctx.SaveChangesAsync();

        await service.AllocateConfirmedPenaltyToLinesAsync(bc, 300m, "ARS", CancellationToken.None);
        await ctx.SaveChangesAsync();

        var line = await ctx.Set<BookingCancellationLine>().AsNoTracking().FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(0m, line.RefundCap);
        Assert.Null(line.PenaltyAmount);
    }
}
