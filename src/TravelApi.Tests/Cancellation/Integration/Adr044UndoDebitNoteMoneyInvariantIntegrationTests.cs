using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-044 "Deshacer una multa ya emitida" — INVARIANTE DE PLATA end-to-end contra Postgres REAL (bloqueante de
/// seguridad, corrección post-gate 2026-07-14). InMemory cortocircuita el lock <c>FOR UPDATE</c> y NO aplica el
/// índice único parcial; sólo acá se validan de verdad.
///
/// <para><b>Investigación del camino real de cobro de una multa (documentada en el test)</b>: el ÚNICO alta de
/// cobro del producto (<c>PaymentService.CreatePaymentAsync</c> / <c>ReservaService.AddPaymentAsync</c>) exige
/// <c>Reserva.EnsureCollectable()</c> → estado de VENTA FIRME ({InManagement, Confirmed, Closed}), que EXCLUYE
/// Cancelled/PendingOperatorRefund. Por eso HOY NO EXISTE camino para registrar un cobro contra la multa de una
/// reserva anulada: la multa se emite (ND) pero no se "cobra" por la UI de cobranza de la anulada. En
/// consecuencia, al deshacer, la porción cobrada es 0 y NO se acuña crédito (rama "pagada" defensiva para un
/// futuro camino de cobro). Los tests afirman exactamente eso, y que no aparece crédito fantasma.</para>
///
/// <para><b>Requiere Docker</b> (Testcontainers Postgres). Si Docker no está disponible, estos tests no corren
/// (el gate de CI los corre antes del deploy).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr044UndoDebitNoteMoneyInvariantIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr044UndoDebitNoteMoneyInvariantIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>BookingCancellationService con un invoice mock FIEL: al pedir la NC, inserta la fila de la NC en
    /// la BD real (Resultado=PENDING) apuntando al comprobante original del request, como el pipeline real.</summary>
    private BookingCancellationService BuildService(AppDbContext ctx, Mock<IAuditService>? auditMock = null)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        invoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                using var inner = _fixture.CreateDbContext();
                var original = inner.Invoices.First(i => i.PublicId == Guid.Parse(req.OriginalInvoiceId!));
                var nc = new Invoice
                {
                    TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 9000 + original.Id,
                    Resultado = "PENDING", ReservaId = original.ReservaId, OriginalInvoiceId = original.Id,
                    MonId = req.MonId, MonCotiz = req.MonCotiz, CreatedAt = DateTime.UtcNow,
                };
                inner.Invoices.Add(nc);
                inner.SaveChanges();
                return new InvoiceDto { PublicId = nc.PublicId };
            });

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnableCancellationDebitNote = true,
                OperatorRefundTimeoutDays = 60,
            });

        return new BookingCancellationService(
            ctx, invoiceMock.Object, new ApprovalRequestService(ctx, settingsMock.Object),
            (auditMock ?? new Mock<IAuditService>()).Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object, new Mock<IAdminUserCountService>().Object);
    }

    /// <summary>
    /// Siembra el estado "reserva anulada con una ND de multa emitida (CAE)": reserva Cancelled, factura C
    /// original con CAE, ND C=12 con CAE vinculada al BC (multa Confirmed). Devuelve (bcPublicId, ndId, reservaId,
    /// customerId). Opcionalmente siembra una fila de saldo por moneda (para el caso parcial).
    /// </summary>
    private async Task<(Guid BcPublicId, int NdId, int ReservaId, int CustomerId)> SeedIssuedPenaltyAsync(
        decimal grossPenalty = 30_000m, decimal? penaltyCurrencyBalance = null)
    {
        await using var ctx = _fixture.CreateDbContext();
        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        // Reserva anulada.
        var reserva = await ctx.Reservas.FirstAsync(r => r.Id == resId);
        reserva.Status = EstadoReserva.Cancelled;
        await ctx.SaveChangesAsync();

        var nd = new Invoice
        {
            TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 500, CAE = "nd-cae", Resultado = "A",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = grossPenalty, ReservaId = resId, OriginalInvoiceId = invId,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(nd);
        await ctx.SaveChangesAsync();
        ctx.Set<InvoiceItem>().Add(new InvoiceItem
        {
            InvoiceId = nd.Id, Description = "Multa por cancelación", Quantity = 1, UnitPrice = grossPenalty,
            Total = grossPenalty, AlicuotaIvaId = 3,
        });
        await ctx.SaveChangesAsync();

        var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId,
            BookingCancellationStatus.AwaitingOperatorRefund);
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.PenaltyAmountAtEvent = grossPenalty;
        bc.PenaltyCurrencyAtEvent = "PES";
        bc.DebitNoteInvoiceId = nd.Id;
        bc.DebitNoteStatus = DebitNoteStatus.Issued;
        bc.ConfirmedByUserId = "admin";
        bc.ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-20);
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        if (penaltyCurrencyBalance.HasValue)
        {
            ctx.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
            {
                ReservaId = resId, Currency = "ARS", Balance = penaltyCurrencyBalance.Value,
            });
            await ctx.SaveChangesAsync();
        }

        return (bc.PublicId, nd.Id, resId, custId);
    }

    /// <summary>Ejecuta el deshacer + la reconciliación (CAE de la NC) — el flujo end-to-end real.</summary>
    private async Task UndoAndReconcileAsync(Guid bcPublicId, Mock<IAuditService>? auditMock = null)
    {
        await using (var ctx = _fixture.CreateDbContext())
        {
            var service = BuildService(ctx, auditMock);
            await service.UndoIssuedDebitNoteAsync(
                bcPublicId, "El monto de la multa estaba mal.", "admin", "Admin", CancellationToken.None,
                requesterIsAdmin: true);
        }

        // Simular el CAE de la NC + correr el reconciliador real (bajo lock Postgres).
        await using (var ctx = _fixture.CreateDbContext())
        {
            var annulment = await ctx.Set<BookingCancellationDebitNoteAnnulment>()
                .OrderByDescending(a => a.Id).FirstAsync();
            var nc = await ctx.Invoices.FirstAsync(i => i.Id == annulment.AnnulmentCreditNoteInvoiceId);
            nc.Resultado = "A";
            nc.CAE = "nc-undo-cae";
            await ctx.SaveChangesAsync();

            await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
                ctx, nc, (auditMock ?? new Mock<IAuditService>()).Object, NullLogger.Instance, CancellationToken.None);
        }
    }

    // (a) DOCUMENTACIÓN del camino real: no existe forma de cobrar la multa de una anulada.
    [Fact]
    public void ThereIsNoProductPathToCollectAPenaltyOfAnAnnulledReserva()
    {
        // El gate REAL de todo alta de cobro (PaymentService.CreatePaymentAsync / ReservaService.AddPaymentAsync)
        // es Reserva.EnsureCollectable(). Sobre una reserva anulada tira -> no se puede registrar el cobro de la
        // multa. Este es el hecho que hace que, en la práctica, el deshacer acuñe 0.
        var annulled = new Reserva { Status = EstadoReserva.Cancelled, Balance = 30_000m };
        Assert.Throws<InvalidOperationException>(() => annulled.EnsureCollectable());

        var pendingRefund = new Reserva { Status = EstadoReserva.PendingOperatorRefund, Balance = 30_000m };
        Assert.Throws<InvalidOperationException>(() => pendingRefund.EnsureCollectable());
    }

    // (a)+(b)+(c) Reserva anulada saldada (el caso NORMAL, sin cobro de multa) -> undo acuña 0, sin fantasmas.
    [Fact]
    public async Task Undo_SettledAnnulledReserva_MintsNothing_NoPhantomCreditNorDebt_NoCoexistingOverpaymentCredit()
    {
        var (bcPublicId, ndId, reservaId, customerId) = await SeedIssuedPenaltyAsync(
            grossPenalty: 30_000m, penaltyCurrencyBalance: 0m);

        await UndoAndReconcileAsync(bcPublicId);

        await using var verify = _fixture.CreateDbContext();

        // La ND quedó desvinculada (deshacer consumado) y la fila hija Succeeded.
        var bc = await verify.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPublicId);
        Assert.Null(bc.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.NotApplicable, bc.DebitNoteStatus);
        var annulment = await verify.Set<BookingCancellationDebitNoteAnnulment>().AsNoTracking()
            .OrderByDescending(a => a.Id).FirstAsync();
        Assert.Equal(DebitNoteAnnulmentStatus.Succeeded, annulment.Status);

        // (a) mint == exactamente lo cobrado == 0 (no hay camino para cobrar la multa de una anulada).
        var credits = await verify.ClientCreditEntries.AsNoTracking()
            .Where(e => e.CustomerId == customerId).ToListAsync();
        Assert.Empty(credits);

        // (b) sin puente fantasma (ni deuda ni crédito inventado en ReservaMoneyByCurrency).
        Assert.False(await verify.Payments.AsNoTracking().AnyAsync(p => p.Method == "MultaDeshecha"));

        // (c) no coexiste ningún crédito de sobrepago (SourcePaymentId) con uno del undo.
        Assert.False(credits.Any(c => c.SourcePaymentId != null));
        Assert.False(credits.Any(c => c.SourceDebitNoteAnnulmentId != null));
    }

    // (a) invariante de plata en el caso PARCIAL (defensivo): mint == exactamente gross - saldo por cobrar.
    [Fact]
    public async Task Undo_PartialPositiveBalance_MintsExactlyCollectedPortion_AndBalanceStaysCoherent()
    {
        // gross 30000, saldo aún por cobrar 10000 -> cobrado 20000. Único caso que acuña algo.
        var (bcPublicId, _, reservaId, customerId) = await SeedIssuedPenaltyAsync(
            grossPenalty: 30_000m, penaltyCurrencyBalance: 10_000m);

        await UndoAndReconcileAsync(bcPublicId);

        await using var verify = _fixture.CreateDbContext();

        var credit = await verify.ClientCreditEntries.AsNoTracking().SingleAsync(e => e.CustomerId == customerId);
        Assert.Equal(20_000m, credit.CreditedAmount);            // == exactamente lo cobrado
        Assert.Equal(20_000m, credit.RemainingBalance);
        Assert.NotNull(credit.SourceDebitNoteAnnulmentId);
        Assert.Null(credit.BookingCancellationId);               // no dispara B5
        Assert.Null(credit.OperatorRefundAllocationId);

        // Puente negativo por lo cobrado (no mueve caja).
        var bridge = await verify.Payments.AsNoTracking().SingleAsync(p => p.Method == "MultaDeshecha");
        Assert.Equal(-20_000m, bridge.Amount);
        Assert.False(bridge.AffectsCash);

        // (c) el único crédito es el del undo; no coexiste uno de sobrepago.
        Assert.False(await verify.ClientCreditEntries.AsNoTracking()
            .AnyAsync(e => e.CustomerId == customerId && e.SourcePaymentId != null));
    }

    // Concurrencia (patrón Adr042MultiInvoiceConcurrency): dos deshacer casi simultáneos -> UNA fila Pending; el
    // segundo rebota INV-UNDO-002 (índice único parcial real + lock del padre).
    [Fact]
    public async Task Undo_TwoConcurrentRequests_OnlyOneLiveAnnulment_SecondRebounds()
    {
        var (bcPublicId, _, _, _) = await SeedIssuedPenaltyAsync(grossPenalty: 30_000m, penaltyCurrencyBalance: 0m);

        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();
        var serviceA = BuildService(ctxA);
        var serviceB = BuildService(ctxB);

        var results = await Task.WhenAll(
            RunUndoCatchingAsync(serviceA, bcPublicId),
            RunUndoCatchingAsync(serviceB, bcPublicId));

        // Exactamente uno tuvo éxito; el otro rebotó (409 de negocio o el 23505 del índice único mapeado).
        Assert.Equal(1, results.Count(ok => ok));

        await using var verify = _fixture.CreateDbContext();
        var live = await verify.Set<BookingCancellationDebitNoteAnnulment>().AsNoTracking()
            .CountAsync(a => a.Status != DebitNoteAnnulmentStatus.Failed);
        Assert.Equal(1, live); // a lo sumo UNA anulación viva por ND (índice único parcial).
    }

    private static async Task<bool> RunUndoCatchingAsync(BookingCancellationService service, Guid bcPublicId)
    {
        try
        {
            await service.UndoIssuedDebitNoteAsync(
                bcPublicId, "Deshacer concurrente.", "admin", "Admin", CancellationToken.None,
                requesterIsAdmin: true);
            return true;
        }
        catch (BusinessInvariantViolationException) { return false; } // INV-UNDO-002
        catch (DbUpdateException) { return false; }                    // 23505 del índice único
        catch (InvalidOperationException) { return false; }            // carrera de lock/estado
    }
}
