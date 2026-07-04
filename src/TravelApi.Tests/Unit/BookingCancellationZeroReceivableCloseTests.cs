using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-07-03) Cierre AUTOMATICO de anulaciones SIN reembolso pendiente del operador.
///
/// <para>Problema (caso real prod #F-2026-1025): cuando la agencia NUNCA le pago nada al operador por un viaje, al
/// anular la reserva el receivable "me tiene que devolver" es $0, pero la anulacion quedaba trabada en
/// <c>AwaitingOperatorRefund</c> para siempre (no se puede registrar reembolso ni pagar al operador). Ahora, si el
/// receivable vivo es $0 en todas las monedas y no hay multa pendiente, se cierra directo: BC -> <c>Closed</c> y
/// reserva -> <c>Cancelled</c>. Se dispara en la transicion post-CAE (via los callbacks de ARCA) y en el barrido
/// nocturno (<see cref="BookingCancellationService.CloseZeroReceivableCancellationsAsync"/>).</para>
///
/// <para>InMemory de EF (mismo trade-off que el resto de la suite de cancelacion): no valida CHECK SQL ni el
/// FOR UPDATE, pero alcanza para la logica de guard + transicion + idempotencia. El calculo del receivable usa la
/// fuente unica <c>SupplierCancellationCircuitReader</c>, que corre igual en InMemory.</para>
/// </summary>
public class BookingCancellationZeroReceivableCloseTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bc-zero-receivable-close-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (BookingCancellationService Service, Mock<IAuditService> AuditMock) BuildService(
        AppDbContext ctx, bool debitNoteEnabled = false)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        var approvalMock = new Mock<IApprovalRequestService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        var calculatorMock = new Mock<IFiscalLiquidationCalculator>();
        var adminCountMock = new Mock<IAdminUserCountService>();

        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnableCancellationDebitNote = debitNoteEnabled,
                OperatorRefundTimeoutDays = 60,
            });

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalMock.Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            calculatorMock.Object, adminCountMock.Object);

        return (service, auditMock);
    }

    /// <summary>
    /// Arma una reserva + BC + factura originante + lineas. El BC nace en <paramref name="bcStatus"/> y la reserva
    /// en <paramref name="reservaStatus"/>. Cada linea es (RefundCap, Received): con RefundCap=0 no hay nada que el
    /// operador deba devolver (receivable $0). Devuelve (bc, invoice) para poder disparar el callback de ARCA.
    /// </summary>
    private static async Task<(BookingCancellation Bc, Invoice OriginatingInvoice)> SeedAsync(
        AppDbContext ctx,
        IReadOnlyList<(decimal RefundCap, decimal Received)> lines,
        BookingCancellationStatus bcStatus,
        string reservaStatus = EstadoReserva.PendingOperatorRefund,
        PenaltyStatus penaltyStatus = PenaltyStatus.Confirmed,
        DebitNoteStatus debitNoteStatus = DebitNoteStatus.NotApplicable,
        int? creditNoteInvoiceId = 999,
        string numero = "R-ZERO",
        // (2026-07-04) Reembolso a nivel CABECERA: SOLO relevante para las BC legacy SIN lineas (pre-backfill),
        // donde el esperado/recibido se registro en el BC padre en vez de en lineas.
        decimal estimatedRefundAmount = 0m,
        decimal receivedRefundAmount = 0m)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = numero, Name = numero, PayerId = customer.Id, Status = reservaStatus };
        var invoice = new Invoice { TipoComprobante = 1, Resultado = "A", CAE = "12345678901234" };
        ctx.Reservas.Add(reserva);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            CreditNoteInvoiceId = creditNoteInvoiceId,
            Status = bcStatus,
            PenaltyStatus = penaltyStatus,
            DebitNoteStatus = debitNoteStatus,
            Reason = "Anulacion sin plata pagada al operador",
            DraftedByUserId = "vendedor-1",
            DraftedByUserName = "Juan Vendedor",
            OperatorRefundDueBy = DateTime.UtcNow.AddDays(30),
            EstimatedRefundAmount = estimatedRefundAmount,
            ReceivedRefundAmount = receivedRefundAmount,
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        foreach (var (refundCap, received) in lines)
        {
            ctx.BookingCancellationLines.Add(new BookingCancellationLine
            {
                BookingCancellationId = bc.Id,
                SupplierId = supplier.Id,
                ServiceTable = CancellableServiceTable.Hotel,
                ServiceId = 1,
                Scope = BookingCancellationLineScope.Full,
                Currency = "USD",
                LineSaleAmount = refundCap,
                RefundCap = refundCap,
                ReceivedRefundAmount = received,
            });
        }
        await ctx.SaveChangesAsync();

        return (bc, invoice);
    }

    // ============================ TRANSICION post-CAE ============================

    [Fact]
    public async Task Transicion_ReceivableCero_SinMultaPendiente_CierraDirecto()
    {
        // Agencia nunca le pago nada al operador -> RefundCap 0 -> receivable $0. Flag de ND apagado -> sin multa
        // pendiente. Al confirmar la NC (callback de ARCA), en vez de "esperando reembolso" se cierra directo.
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx, debitNoteEnabled: false);
        var (bc, invoice) = await SeedAsync(ctx,
            lines: new[] { (RefundCap: 0m, Received: 0m) },
            bcStatus: BookingCancellationStatus.AwaitingFiscalConfirmation);

        await service.OnArcaSucceededAsync(invoice.Id, creditNoteInvoiceId: 555, CancellationToken.None);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.Closed, bcAfter.Status);
        Assert.NotNull(bcAfter.ClosedAt);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

        // Rastro del cambio de estado de la reserva, con motivo en criollo (visible en el historial).
        var log = await ctx.ReservaStatusChangeLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ReservaId == bc.ReservaId && l.ToStatus == EstadoReserva.Cancelled);
        Assert.NotNull(log);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, log!.FromStatus);
        Assert.Contains("no había pagos al operador", log.Reason);

        // Auditoria dedicada del cierre por $0.
        auditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.BookingCancellationClosedNoOperatorRefundDue,
            AuditActions.BookingCancellationEntityName,
            bc.Id.ToString(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Transicion_ConReceivable_SigueEsperandoReembolso_SinRegresion()
    {
        // Hay plata que el operador debe devolver (RefundCap 400, recibido 0). NO se cierra: sigue el camino normal
        // a AwaitingOperatorRefund + reserva PendingOperatorRefund.
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx, debitNoteEnabled: false);
        var (bc, invoice) = await SeedAsync(ctx,
            lines: new[] { (RefundCap: 400m, Received: 0m) },
            bcStatus: BookingCancellationStatus.AwaitingFiscalConfirmation);

        await service.OnArcaSucceededAsync(invoice.Id, creditNoteInvoiceId: 555, CancellationToken.None);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcAfter.Status);
        Assert.Null(bcAfter.ClosedAt);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reserva.Status);

        auditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.BookingCancellationClosedNoOperatorRefundDue,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Transicion_ReceivableCero_ConMultaSinDecidir_CierraIgual()
    {
        // (2026-07-04, DECISION DEL DUEÑO) Antes: receivable $0 + multa sin decidir (Estimated, flag ON) NO cerraba
        // porque la multa bloqueaba. Ahora: cuando NUNCA hubo circuito con el operador, se cierra IGUAL y la
        // pregunta de la multa queda como TAREA pendiente (se resuelve despues, desde el estado Closed). Solo una ND
        // a medio emitir (Pending/Failed) sigue bloqueando — este caso no la tiene.
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx, debitNoteEnabled: true);
        var (bc, invoice) = await SeedAsync(ctx,
            lines: new[] { (RefundCap: 0m, Received: 0m) },
            bcStatus: BookingCancellationStatus.AwaitingFiscalConfirmation,
            penaltyStatus: PenaltyStatus.Estimated);

        await service.OnArcaSucceededAsync(invoice.Id, creditNoteInvoiceId: 555, CancellationToken.None);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        // Cierre directo (la multa ya no bloquea).
        Assert.Equal(BookingCancellationStatus.Closed, bcAfter.Status);
        Assert.NotNull(bcAfter.ClosedAt);
        // La multa NO se toca: sigue sin decidir (se resuelve despues del cierre).
        Assert.Equal(PenaltyStatus.Estimated, bcAfter.PenaltyStatus);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);

        // La pata de la multa SIGUE operable: el outcome del read-model da "Pending" aun con la reserva cerrada
        // (el cartel "falta decidir la multa" se muestra igual).
        Assert.Equal(
            OperatorPenaltyOutcome.Pending,
            await service.GetOperatorPenaltyOutcomeAsync(reserva.PublicId, CancellationToken.None));

        auditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.BookingCancellationClosedNoOperatorRefundDue,
            AuditActions.BookingCancellationEntityName,
            bc.Id.ToString(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Transicion_ReceivableCero_ConNdAMedioEmitir_NoCierra()
    {
        // Contra-caso: una ND a medio emitir (Failed) SIGUE bloqueando el cierre (documento fiscal a medias). Se
        // resuelve por la bandeja / el reintento; recien ahi se cierra.
        await using var ctx = NewDbContext();
        var (service, auditMock) = BuildService(ctx, debitNoteEnabled: true);
        var (bc, invoice) = await SeedAsync(ctx,
            lines: new[] { (RefundCap: 0m, Received: 0m) },
            bcStatus: BookingCancellationStatus.AwaitingFiscalConfirmation,
            penaltyStatus: PenaltyStatus.Confirmed,
            debitNoteStatus: DebitNoteStatus.Failed);

        await service.OnArcaSucceededAsync(invoice.Id, creditNoteInvoiceId: 555, CancellationToken.None);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcAfter.Status);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reserva.Status);

        auditMock.Verify(a => a.StageBusinessEvent(
            AuditActions.BookingCancellationClosedNoOperatorRefundDue,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // ============================ BARRIDO nocturno ============================

    [Fact]
    public async Task Barrido_CierraAwaitingYAbandonedConCero_NoTocaConReceivableNiMulta()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx, debitNoteEnabled: true);

        // (1) Awaiting con $0 y sin multa pendiente (multa ya resuelta = Confirmed) -> se cierra.
        var (awaitingZero, _) = await SeedAsync(ctx,
            lines: new[] { (0m, 0m) }, bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            penaltyStatus: PenaltyStatus.Confirmed, numero: "R-AWAIT-ZERO");

        // (2) Abandoned con $0 -> se cierra.
        var (abandonedZero, _) = await SeedAsync(ctx,
            lines: new[] { (0m, 0m) }, bcStatus: BookingCancellationStatus.AbandonedByOperator,
            penaltyStatus: PenaltyStatus.Confirmed, numero: "R-ABAND-ZERO");

        // (3) Awaiting con receivable > 0 -> NO se toca.
        var (awaitingOwed, _) = await SeedAsync(ctx,
            lines: new[] { (400m, 0m) }, bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            penaltyStatus: PenaltyStatus.Confirmed, numero: "R-AWAIT-OWED");

        // (4) Awaiting con $0 PERO ND a medio emitir (Failed) -> NO se toca (caso #F-2026-1025).
        var (awaitingBadNd, _) = await SeedAsync(ctx,
            lines: new[] { (0m, 0m) }, bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            penaltyStatus: PenaltyStatus.Confirmed, debitNoteStatus: DebitNoteStatus.Failed, numero: "R-AWAIT-BADND");

        var closed = await service.CloseZeroReceivableCancellationsAsync(CancellationToken.None);
        Assert.Equal(2, closed);

        await AssertClosedAsync(ctx, awaitingZero.Id);
        await AssertClosedAsync(ctx, abandonedZero.Id);
        await AssertNotClosedAsync(ctx, awaitingOwed.Id, BookingCancellationStatus.AwaitingOperatorRefund);
        await AssertNotClosedAsync(ctx, awaitingBadNd.Id, BookingCancellationStatus.AwaitingOperatorRefund);

        // Idempotencia: una segunda corrida no vuelve a cerrar nada.
        var closedAgain = await service.CloseZeroReceivableCancellationsAsync(CancellationToken.None);
        Assert.Equal(0, closedAgain);

        // Un solo log de cierre por reserva cerrada (no se duplica).
        var closeLogs = await ctx.ReservaStatusChangeLogs.AsNoTracking()
            .CountAsync(l => l.ReservaId == awaitingZero.ReservaId && l.ToStatus == EstadoReserva.Cancelled);
        Assert.Equal(1, closeLogs);
    }

    [Fact]
    public async Task Barrido_NoCierra_BcTotalmenteReembolsado_NiConCapEnOtraMoneda()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx, debitNoteEnabled: false);

        // (M1 security review 2026-07-03) BC TOTALMENTE reembolsado (cap == recibido > 0): NO se cierra por el
        // barrido. Cerrarlo dejaria sin camino de UI el void de esa allocation (si el reembolso registrado
        // despues rebota, OnAllocationVoidedAsync no reabre desde Closed). Su via de cierre es la normal
        // (aplicacion del credito -> OnAllCreditConsumedAsync). El cierre automatico es SOLO para el caso
        // "nunca hubo circuito con el operador" (todas las lineas cap == 0 y recibido == 0).
        var (fullyRefunded, _) = await SeedAsync(ctx,
            lines: new[] { (400m, 400m) }, bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            numero: "R-FULLREF");

        // (review backend 2026-07-03) BC con una linea en $0 y OTRA con cap vivo: cualquier cap > 0 bloquea el
        // cierre (la guarda es por TODAS las lineas, no por moneda).
        var (mixedCaps, _) = await SeedAsync(ctx,
            lines: new[] { (0m, 0m), (250m, 0m) }, bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            numero: "R-MIXCAP");

        var closed = await service.CloseZeroReceivableCancellationsAsync(CancellationToken.None);

        Assert.Equal(0, closed);
        await AssertNotClosedAsync(ctx, fullyRefunded.Id, BookingCancellationStatus.AwaitingOperatorRefund);
        await AssertNotClosedAsync(ctx, mixedCaps.Id, BookingCancellationStatus.AwaitingOperatorRefund);
    }

    [Fact]
    public async Task Barrido_AbandonedConReservaYaCancelada_CierraBcSinRetocarLaReserva()
    {
        // Estado real de produccion: el abandono por timeout ya dejo la reserva en Cancelled. El barrido cierra
        // el BC igual (sale del limbo) pero NO re-mueve la reserva ni duplica el log de cambio de estado.
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx, debitNoteEnabled: false);
        var (bc, _) = await SeedAsync(ctx,
            lines: new[] { (0m, 0m) }, bcStatus: BookingCancellationStatus.AbandonedByOperator,
            reservaStatus: EstadoReserva.Cancelled, numero: "R-ABAND-CANC");

        var closed = await service.CloseZeroReceivableCancellationsAsync(CancellationToken.None);
        Assert.Equal(1, closed);

        await AssertClosedAsync(ctx, bc.Id);
        // Sin log nuevo de cambio de estado: la reserva ya estaba Cancelled (rama idempotente).
        var logs = await ctx.ReservaStatusChangeLogs.AsNoTracking()
            .CountAsync(l => l.ReservaId == bc.ReservaId);
        Assert.Equal(0, logs);
    }

    [Fact]
    public async Task Barrido_CorreDentroDelJobDeTimeout()
    {
        // El paso de cierre por $0 va en la MISMA corrida que el abandono por timeout: al invocar
        // ProcessExpiredOperatorRefundsAsync tambien se cierran las trabadas sin receivable.
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx, debitNoteEnabled: false);
        var (bc, _) = await SeedAsync(ctx,
            lines: new[] { (0m, 0m) }, bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            numero: "R-JOB-ZERO");

        await service.ProcessExpiredOperatorRefundsAsync(CancellationToken.None);

        await AssertClosedAsync(ctx, bc.Id);
    }

    // ============================ BC legacy SIN lineas (esperado a nivel cabecera) ============================

    [Fact]
    public async Task Barrido_SinLineas_EsperadoCabeceraCero_Cierra()
    {
        // (2026-07-04, CAMBIO 2) BC legacy pre-backfill: sin lineas, el esperado/recibido se registro en la CABECERA.
        // Con esperado 0 y recibido 0 tampoco hubo circuito con el operador -> se cierra igual que las modernas $0.
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx, debitNoteEnabled: false);
        var (bc, _) = await SeedAsync(ctx,
            lines: System.Array.Empty<(decimal, decimal)>(),
            bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            estimatedRefundAmount: 0m,
            receivedRefundAmount: 0m,
            numero: "R-LEGACY-ZERO");

        var closed = await service.CloseZeroReceivableCancellationsAsync(CancellationToken.None);
        Assert.Equal(1, closed);
        await AssertClosedAsync(ctx, bc.Id);
    }

    [Fact]
    public async Task Barrido_SinLineas_EsperadoCabeceraPositivo_NoCierra()
    {
        // Contra-caso: la cabecera esperaba un reembolso real (> 0) que la vista por-linea no ve (no hay lineas).
        // NO se cierra: hay un receivable real; se resuelve a mano.
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx, debitNoteEnabled: false);
        var (bc, _) = await SeedAsync(ctx,
            lines: System.Array.Empty<(decimal, decimal)>(),
            bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            estimatedRefundAmount: 250_000m,
            receivedRefundAmount: 0m,
            numero: "R-LEGACY-OWED");

        var closed = await service.CloseZeroReceivableCancellationsAsync(CancellationToken.None);
        Assert.Equal(0, closed);
        await AssertNotClosedAsync(ctx, bc.Id, BookingCancellationStatus.AwaitingOperatorRefund);
    }

    // ============================ Job dedicado (recurring propio) ============================

    [Fact]
    public async Task JobDedicado_CorreElBarrido()
    {
        // (2026-07-04, CAMBIO 3) El barrido tiene su propio job recurrente (desacoplado del de timeouts). Verificamos
        // que el wrapper de Hangfire ejecuta el barrido de dominio y cierra las trabadas sin receivable.
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx, debitNoteEnabled: false);
        var (bc, _) = await SeedAsync(ctx,
            lines: new[] { (0m, 0m) }, bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            numero: "R-JOBPROPIO-ZERO");

        var job = new ZeroReceivableCancellationCloseJob(
            service, NullLogger<ZeroReceivableCancellationCloseJob>.Instance);
        await job.RunAsync(CancellationToken.None);

        await AssertClosedAsync(ctx, bc.Id);
    }

    // ============================ Read-model (solapa "Reembolsos") ============================

    [Fact]
    public async Task Barrido_BcCerradoDesapareceDelReadModelDeReembolsos()
    {
        await using var ctx = NewDbContext();
        var (service, _) = BuildService(ctx, debitNoteEnabled: false);
        var (bc, _) = await SeedAsync(ctx,
            lines: new[] { (0m, 0m) }, bcStatus: BookingCancellationStatus.AwaitingOperatorRefund,
            numero: "R-READMODEL");
        var supplierId = bc.SupplierId;

        // Antes: aparece en la solapa (esta AwaitingOperatorRefund).
        var readModel = new OperatorRefundReadModelService(ctx, httpContextAccessor: null, permissionResolver: null);
        var before = await readModel.GetSupplierPendingRefundsAsync(supplierId, CancellationToken.None);
        Assert.Single(before);

        var closed = await service.CloseZeroReceivableCancellationsAsync(CancellationToken.None);
        Assert.Equal(1, closed);

        // Despues: cerrado y sin residuo -> el WHERE del read-model lo excluye.
        var after = await readModel.GetSupplierPendingRefundsAsync(supplierId, CancellationToken.None);
        Assert.Empty(after);
    }

    // ============================ Helpers de asercion ============================

    private static async Task AssertClosedAsync(AppDbContext ctx, int bcId)
    {
        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.Equal(BookingCancellationStatus.Closed, bc.Status);
        Assert.NotNull(bc.ClosedAt);
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == bc.ReservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);
    }

    private static async Task AssertNotClosedAsync(AppDbContext ctx, int bcId, BookingCancellationStatus expected)
    {
        var bc = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.Equal(expected, bc.Status);
        Assert.Null(bc.ClosedAt);
    }
}
