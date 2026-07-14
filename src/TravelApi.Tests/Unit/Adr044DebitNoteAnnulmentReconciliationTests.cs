using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
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
/// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): tests UNIT de <see cref="DebitNoteAnnulmentReconciliation"/>
/// (molde de <c>CancellationDebitNoteReconciliationTests</c>) y del guard de especificidad M2 en
/// <see cref="AfipService.ApplyCreditNoteEconomicReversalAsync"/>. InMemory + mocks, sin Docker (la concurrencia
/// real bajo lock Postgres — B3/test 20 — se valida en integración, fuera de esta suite).
/// </summary>
public class Adr044DebitNoteAnnulmentReconciliationTests
{
    private static AppDbContext NewDbContext(string? dbName = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName ?? $"undo-reconcile-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed class NoopSensitiveDataProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }

    private static AfipService BuildAfipService(AppDbContext ctx, IAuditService? auditService = null) =>
        new(ctx, NullLogger<AfipService>.Instance, new HttpClient(), new NoopSensitiveDataProtector(), auditService);

    /// <summary>
    /// Semilla completa: reserva Cancelled, factura original con CAE, ND (C=12) Issued vinculada al BC (con multa
    /// Confirmed), y la NC-anula-ND (C=13, OriginalInvoiceId=nd) + su fila hija <see cref="BookingCancellationDebitNoteAnnulment"/>
    /// en Pending. <paramref name="balanceInCurrency"/> es el saldo de <c>ReservaMoneyByCurrency</c> en la moneda
    /// de la multa (null = sin fila, cae al fallback "bruto completo" = multa impaga).
    /// </summary>
    private static async Task<(BookingCancellation Bc, Invoice Nd, Invoice Nc, BookingCancellationDebitNoteAnnulment Annulment, Reserva Reserva, Customer Customer)>
        SeedPendingAnnulmentAsync(
            AppDbContext ctx, decimal grossPenalty = 30_000m, decimal? balanceInCurrency = null,
            string currency = "ARS")
    {
        var customer = new Customer { FullName = "Cliente Reconciliar", IsActive = true };
        var supplier = new Supplier { Name = "Operador Reconciliar", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-RECONCILE", Name = "Reserva Reconciliar", PayerId = customer.Id,
            Status = EstadoReserva.Cancelled, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 100, CAE = "12345678", Resultado = "A",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = 100_000m, ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        var nd = new Invoice
        {
            TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 200, CAE = "55555555", Resultado = "A",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = grossPenalty, ReservaId = reserva.Id,
            OriginalInvoiceId = original.Id,
        };
        ctx.Invoices.Add(nd);
        await ctx.SaveChangesAsync();

        var nc = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 900, Resultado = "PENDING",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = grossPenalty, ReservaId = reserva.Id,
            OriginalInvoiceId = nd.Id,
        };
        ctx.Invoices.Add(nc);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id, Status = BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyStatus = PenaltyStatus.Confirmed, PenaltyAmountAtEvent = grossPenalty,
            PenaltyCurrencyAtEvent = "PES", DebitNoteInvoiceId = nd.Id, DebitNoteStatus = DebitNoteStatus.Issued,
            Reason = "Anulacion con multa emitida", DraftedByUserId = "vendedor-1",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var annulment = new BookingCancellationDebitNoteAnnulment
        {
            BookingCancellationId = bc.Id, AnnulledDebitNoteInvoiceId = nd.Id,
            AnnulmentCreditNoteInvoiceId = nc.Id, Status = DebitNoteAnnulmentStatus.Pending,
            Reason = "La multa estaba mal.", Amount = grossPenalty, Currency = "ARS",
            RequestedByUserId = "u", RequestedByUserName = "U",
        };
        ctx.Set<BookingCancellationDebitNoteAnnulment>().Add(annulment);
        await ctx.SaveChangesAsync();

        if (balanceInCurrency.HasValue)
        {
            ctx.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
            {
                ReservaId = reserva.Id, Currency = currency, Balance = balanceInCurrency.Value,
            });
            await ctx.SaveChangesAsync();
        }

        return (bc, nd, nc, annulment, reserva, customer);
    }

    // ============================================================
    // Test 3 (spec) — ARCA aprueba: Succeeded + desvincula + BC/paso vuelven a abierto.
    // ============================================================

    [Fact]
    public async Task Approved_SucceedsAndDesvincula_BcReturnsToConfirmedNoDebitNote()
    {
        var ctx = NewDbContext();
        var (bc, nd, nc, annulment, _, _) = await SeedPendingAnnulmentAsync(ctx, balanceInCurrency: 30_000m); // impaga
        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        var changed = await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, changed);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Null(bcAfter.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.NotApplicable, bcAfter.DebitNoteStatus);
        Assert.Null(bcAfter.DebitNoteArcaErrorMessage);

        var annulmentAfter = await ctx.Set<BookingCancellationDebitNoteAnnulment>()
            .AsNoTracking().SingleAsync(a => a.Id == annulment.Id);
        Assert.Equal(DebitNoteAnnulmentStatus.Succeeded, annulmentAfter.Status);
    }

    [Fact]
    public async Task Rejected_MarksFailed_AndLeavesBcIntact()
    {
        var ctx = NewDbContext();
        var (bc, nd, nc, annulment, _, _) = await SeedPendingAnnulmentAsync(ctx);
        nc.Resultado = "R";
        nc.Observaciones = "ARCA rechazo por banda de cotizacion.";
        await ctx.SaveChangesAsync();

        var changed = await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, changed);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        // La ND original NO se toca: sigue Issued y vinculada tal cual estaba.
        Assert.Equal(nd.Id, bcAfter.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.Issued, bcAfter.DebitNoteStatus);

        var annulmentAfter = await ctx.Set<BookingCancellationDebitNoteAnnulment>()
            .AsNoTracking().SingleAsync(a => a.Id == annulment.Id);
        Assert.Equal(DebitNoteAnnulmentStatus.Failed, annulmentAfter.Status);
        Assert.Contains("banda de cotizacion", annulmentAfter.ArcaErrorMessage!);
    }

    [Fact]
    public async Task StillInFlight_NoOp()
    {
        var ctx = NewDbContext();
        var (_, _, nc, annulment, _, _) = await SeedPendingAnnulmentAsync(ctx);
        // Resultado sigue "PENDING" (default de la semilla): la NC no resolvio todavia.

        var changed = await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, changed);
        var annulmentAfter = await ctx.Set<BookingCancellationDebitNoteAnnulment>()
            .AsNoTracking().SingleAsync(a => a.Id == annulment.Id);
        Assert.Equal(DebitNoteAnnulmentStatus.Pending, annulmentAfter.Status);
    }

    [Fact]
    public async Task UnrelatedCreditNote_NoOp_CheapNoRows()
    {
        // Cualquier otra NC (que no anula una ND) no debe tocar nada: 0 filas hijas matchean.
        var ctx = NewDbContext();
        await SeedPendingAnnulmentAsync(ctx);
        var unrelatedNc = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 999, Resultado = "A", CAE = "1",
            ImporteTotal = 500m,
        };
        ctx.Invoices.Add(unrelatedNc);
        await ctx.SaveChangesAsync();

        var changed = await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, unrelatedNc, auditService: null, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, changed);
    }

    // ============================================================
    // B1 (Rev 2) — acuñar saldo a favor por la porcion COBRADA.
    // ============================================================

    [Fact]
    public async Task B1_SettledOrOverpaidReserva_MintsNothing_NoPhantomCredit()
    {
        // Fix bloqueante seguridad (2026-07-14): balance <= 0 (reserva anulada SALDADA, el caso NORMAL en el
        // producto de hoy — no existe camino para cobrar la multa de una anulada) NO significa "multa pagada".
        // La regla ANTERIOR (gross − pendingDisplay, con pendingDisplay clampeado a 0) acuñaba el bruto entero
        // como crédito FANTASMA. La regla nueva (OperatorPenaltyUndoRules) acuña 0.
        var ctx = NewDbContext();
        var (bc, _, nc, _, reserva, customer) = await SeedPendingAnnulmentAsync(
            ctx, grossPenalty: 30_000m, balanceInCurrency: 0m);
        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        // Ni crédito fantasma, ni puente fantasma. La ND igual quedó desvinculada (deshacer consumado).
        Assert.False(await ctx.ClientCreditEntries.AnyAsync(e => e.CustomerId == customer.Id));
        Assert.False(await ctx.Payments.AnyAsync(p => p.Method == "MultaDeshecha"));
        var bcAfter = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Null(bcAfter.DebitNoteInvoiceId);
    }

    [Fact]
    public async Task B1_Impaga_MintsNothing()
    {
        // "Impaga": balance == gross -> multa íntegramente por cobrar -> collected=0 -> ningun credito.
        var ctx = NewDbContext();
        var (_, _, nc, _, _, customer) = await SeedPendingAnnulmentAsync(
            ctx, grossPenalty: 30_000m, balanceInCurrency: 30_000m);
        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        Assert.False(await ctx.ClientCreditEntries.AnyAsync(e => e.CustomerId == customer.Id));
    }

    [Fact]
    public async Task B1_PartiallyCollected_MintsOnlyTheCollectedPortion()
    {
        // Test 16 (Rev 2): gross=30000, saldo aún por cobrar=10000 -> collected=20000. Es el ÚNICO caso que
        // acuña algo (0 < saldo < gross); en el producto de hoy sólo alcanzable con datos parciales legacy.
        var ctx = NewDbContext();
        var (bc, _, nc, _, reserva, customer) = await SeedPendingAnnulmentAsync(
            ctx, grossPenalty: 30_000m, balanceInCurrency: 10_000m);
        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        var credit = await ctx.ClientCreditEntries.AsNoTracking().SingleAsync(e => e.CustomerId == customer.Id);
        Assert.Equal(20_000m, credit.CreditedAmount);
        Assert.Equal(20_000m, credit.RemainingBalance);
        Assert.Null(credit.OperatorRefundAllocationId);
        Assert.Null(credit.BookingCancellationId); // deliberado (guard B5), ver el XML-doc de la entidad.
        Assert.NotNull(credit.SourceDebitNoteAnnulmentId);
        Assert.Equal(reserva.Id, credit.SourceReservaId);
        // Puente negativo que saca la porción cobrada del saldo de la reserva (no mueve caja).
        var bridge = await ctx.Payments.AsNoTracking().SingleAsync(p => p.Method == "MultaDeshecha");
        Assert.Equal(-20_000m, bridge.Amount);
        Assert.False(bridge.AffectsCash);
    }

    [Fact]
    public async Task B1_RetriedByHangfire_DoesNotMintTwice()
    {
        // Test 17 (Rev 2): re-correr el reconciliador (ej. retry de Hangfire tras un crash post-commit) NO
        // acuña un segundo credito para el MISMO evento. Con saldo parcial (10000) SÍ se acuña uno.
        var ctx = NewDbContext();
        var (_, _, nc, annulment, _, customer) = await SeedPendingAnnulmentAsync(
            ctx, grossPenalty: 30_000m, balanceInCurrency: 10_000m);
        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        // Segunda corrida: el reconciliador re-chequea "annulment.Status == Pending" y ya esta Succeeded -> no-op.
        var secondRun = await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, secondRun);
        var credits = await ctx.ClientCreditEntries.Where(e => e.CustomerId == customer.Id).ToListAsync();
        Assert.Single(credits);
    }

    [Fact]
    public async Task B1_WhenACreditAlreadyExistsForThisAnnulment_DoesNotDoubleMint()
    {
        // Test 18 (Rev 2), resuelve el gate (i)/(ii): si por algun motivo YA existe un credito para este evento
        // (idempotencia dura de CreateEntryFromDebitNoteUndoAsync), no se acuña de nuevo. Saldo parcial para que
        // la rama de minteo SÍ se ejercite y sea el guard de idempotencia lo que la frena.
        var ctx = NewDbContext();
        var (bc, _, nc, annulment, reserva, customer) = await SeedPendingAnnulmentAsync(
            ctx, grossPenalty: 30_000m, balanceInCurrency: 10_000m);
        ctx.ClientCreditEntries.Add(new ClientCreditEntry
        {
            CustomerId = customer.Id, SourceReservaId = reserva.Id, SourceDebitNoteAnnulmentId = annulment.Id,
            Currency = "ARS", CreditedAmount = 20_000m, RemainingBalance = 20_000m,
        });
        await ctx.SaveChangesAsync();
        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        var credits = await ctx.ClientCreditEntries.Where(e => e.CustomerId == customer.Id).ToListAsync();
        Assert.Single(credits); // sigue habiendo UNA sola (la pre-existente), no dos.
    }

    // ============================================================
    // B2 (Rev 2) — reset acotado de line.DebitNoteStatus.
    // ============================================================

    [Fact]
    public async Task B2_ResetsOnlyLinesFedByTheUndoneDebitNote_NeverTouchesManualReviewOfAnotherOperator()
    {
        var ctx = NewDbContext();
        var (bc, nd, nc, _, _, _) = await SeedPendingAnnulmentAsync(ctx, balanceInCurrency: 0m);

        var supplierB = new Supplier { Name = "Operador B", IsActive = true };
        ctx.Suppliers.Add(supplierB);
        await ctx.SaveChangesAsync();

        // Linea A (el operador principal del BC): alimento la ND anulada via TargetInvoiceId.
        var lineA = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = bc.SupplierId, Currency = "ARS",
            PenaltyStatus = PenaltyStatus.Confirmed, DebitNoteStatus = DebitNoteStatus.Issued, RefundCap = 10_000m,
        };
        ctx.BookingCancellationLines.Add(lineA);
        await ctx.SaveChangesAsync();
        ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = lineA.Id, Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = PenaltyCollectionMode.Retenida, Amount = 30_000m, Currency = "ARS",
            TargetInvoiceId = nd.Id, ConfirmedByUserId = "u",
        });

        // Linea B (OTRO operador): ManualReview, ND complementaria pendiente, AJENA a la ND que se deshace.
        var lineB = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierB.Id, Currency = "ARS",
            PenaltyStatus = PenaltyStatus.Confirmed, DebitNoteStatus = DebitNoteStatus.ManualReview,
            RefundCap = 5_000m,
        };
        ctx.BookingCancellationLines.Add(lineB);
        await ctx.SaveChangesAsync();
        ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = lineB.Id, Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = PenaltyCollectionMode.Retenida, Amount = 8_000m, Currency = "ARS",
            TargetInvoiceId = null, ConfirmedByUserId = "u",
        });
        await ctx.SaveChangesAsync();

        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        var lineAAfter = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.Id == lineA.Id);
        var lineBAfter = await ctx.BookingCancellationLines.AsNoTracking().SingleAsync(l => l.Id == lineB.Id);

        Assert.Equal(DebitNoteStatus.NotApplicable, lineAAfter.DebitNoteStatus); // reseteada: puede re-emitir.
        Assert.Equal(DebitNoteStatus.ManualReview, lineBAfter.DebitNoteStatus);  // INTACTA: nunca se toco.
    }

    // ============================================================
    // M2 (Rev 2) — guard de especificidad del intercept en AfipService.
    // ============================================================

    [Fact]
    public async Task M2_CreditNoteAgainstADebitNote_DoesNotCreateAnyPaymentReversal_NorVoidAnyReceipt()
    {
        // Test 4 (spec, EL MAS IMPORTANTE): la NC-anula-ND JAMAS debe pasar por la reversion de cobros de FACTURA
        // (eso anularia un recibo y crearia un -Payment, borrando el saldo a favor / duplicando la devolucion).
        var ctx = NewDbContext();
        var (bc, nd, nc, _, reserva, _) = await SeedPendingAnnulmentAsync(ctx, balanceInCurrency: 0m);
        // Un Payment "de cobro" cualquiera en la reserva: si el guard fallara, este seria el candidato a reversar.
        ctx.Payments.Add(new Payment
        {
            ReservaId = reserva.Id, Amount = 30_000m, Currency = "ARS", Status = "Paid",
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true, PaidAt = DateTime.UtcNow,
        });
        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        var afipService = BuildAfipService(ctx);
        await afipService.ApplyCreditNoteEconomicReversalAsync(nc.Id);

        var reversal = await ctx.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.Null(reversal); // NINGUN -Payment de reversion se creo.
    }

    [Fact]
    public async Task M2_ContraTest_CreditNoteAgainstAnInvoice_StillCreatesItsNormalReversal()
    {
        // Contra-test de especificidad (M2): una NC contra una FACTURA (no una ND) SIGUE creando su reversal
        // normal — el guard nuevo NO rompe la anulacion normal de facturas.
        var ctx = NewDbContext();
        var reserva = new Reserva { NumeroReserva = "R-M2", Name = "R-M2", Status = EstadoReserva.Confirmed, Balance = 0m };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var payment = new Payment
        {
            ReservaId = reserva.Id, Amount = 500m, Currency = "ARS", Status = "Paid",
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true, PaidAt = DateTime.UtcNow,
        };
        ctx.Payments.Add(payment);

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 700, Resultado = "A", CAE = "1",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = 500m, ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.Pending,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        var nc = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 701, Resultado = "A", CAE = "2",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = 500m, ReservaId = reserva.Id,
            OriginalInvoiceId = original.Id,
        };
        ctx.Invoices.Add(nc);
        await ctx.SaveChangesAsync();

        var afipService = BuildAfipService(ctx);
        await afipService.ApplyCreditNoteEconomicReversalAsync(nc.Id);

        var reversal = await ctx.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal);
        Assert.NotNull(reversal); // SIGUE creando su reversal normal (regresion cero).
        Assert.Equal(-500m, reversal!.Amount);
    }

    // ============================================================
    // Test 22 (spec) — anti-doble-conteo: la deuda de multa se lee del escalar del BC, nunca sumando la ND vieja.
    // ============================================================

    [Fact]
    public async Task AfterSucceeding_TheOldDebitNoteIsNoLongerCountedAsLiveOnTheBc()
    {
        var ctx = NewDbContext();
        var (bc, nd, nc, _, _, _) = await SeedPendingAnnulmentAsync(ctx, balanceInCurrency: 0m);
        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        // El escalar (fuente unica de "cuenta o no cuenta") ya NO apunta a la ND vieja: cualquier lector que siga
        // el escalar (LiveDebitNotePredicate) deja de contarla. Nadie suma la ND directo por tipo de comprobante.
        Assert.NotEqual(nd.Id, bcAfter.DebitNoteInvoiceId);
        Assert.Null(bcAfter.DebitNoteInvoiceId);
    }

    // ============================================================
    // Point 5 (corrección post-gate) — corner "Succeeded sin mint": ND re-apuntada en carrera.
    // ============================================================

    [Fact]
    public async Task Succeeded_ButBcRepointedToAnotherDebitNote_MarksSucceeded_WithoutDesvinculaNorMint_AndAudits()
    {
        // Entre la solicitud del deshacer y este callback, otro flujo re-apuntó el BC a OTRA ND (ej. se corrigió
        // y re-emitió). La NC igual consiguió CAE (el hecho fiscal es real) -> la fila hija se marca Succeeded,
        // pero NO se desvincula (no pisar el estado nuevo) ni se acuña. El salto NO queda silencioso: audita.
        var ctx = NewDbContext();
        var (bc, nd, nc, annulment, _, customer) = await SeedPendingAnnulmentAsync(
            ctx, grossPenalty: 30_000m, balanceInCurrency: 10_000m); // saldo parcial: minteo SÍ correría si desvinculara
        // Otra ND nueva a la que el BC ahora apunta.
        var newNd = new Invoice
        {
            TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 250, CAE = "88888888", Resultado = "A",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = 30_000m, ReservaId = bc.ReservaId,
            OriginalInvoiceId = bc.OriginatingInvoiceId,
        };
        ctx.Invoices.Add(newNd);
        await ctx.SaveChangesAsync();
        var trackedBc = await ctx.BookingCancellations.SingleAsync(b => b.Id == bc.Id);
        trackedBc.DebitNoteInvoiceId = newNd.Id; // re-apuntada
        await ctx.SaveChangesAsync();

        nc.Resultado = "A";
        nc.CAE = "77777777";
        await ctx.SaveChangesAsync();

        var auditMock = new Mock<IAuditService>();
        var changed = await DebitNoteAnnulmentReconciliation.ReconcileFromCreditNoteAsync(
            ctx, nc, auditMock.Object, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, changed);
        var annulmentAfter = await ctx.Set<BookingCancellationDebitNoteAnnulment>()
            .AsNoTracking().SingleAsync(a => a.Id == annulment.Id);
        Assert.Equal(DebitNoteAnnulmentStatus.Succeeded, annulmentAfter.Status);

        // NO se desvinculó (el BC sigue apuntando a la ND NUEVA) ni se acuñó nada.
        var bcAfter = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(newNd.Id, bcAfter.DebitNoteInvoiceId);
        Assert.False(await ctx.ClientCreditEntries.AnyAsync(e => e.CustomerId == customer.Id));

        // El salto quedó auditado (no silencioso).
        auditMock.Verify(a => a.StageBusinessEvent(
            TravelApi.Application.Constants.AuditActions.OperatorPenaltyDebitNoteUndoNeedsReview,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);
    }
}
