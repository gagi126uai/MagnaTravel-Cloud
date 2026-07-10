using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Spec "el paso de multa vive en la ficha" (2026-07-08) — tests UNIT de las piezas NUEVAS de este encargo:
/// <list type="bullet">
///   <item>A4 <c>CorrectPenaltyAsync</c>: corregir monto + moneda de una multa confirmada con la ND trabada.</item>
///   <item>A6 freno de <c>RevertWaivedOperatorPenaltyAsync</c>: no reabrir si el saldo a favor ya se uso.</item>
///   <item>A3 atribucion del retry al actor real (no al confirmador de la anulacion).</item>
///   <item>A2 <c>GetOperatorPenaltySituationAsync</c>: el read-model del paso de la multa.</item>
/// </list>
/// Mismo enfoque que <see cref="CancellationWaivePenaltyTests"/>: DbContext InMemory + mocks, sin Docker.
/// </summary>
public class CancellationCorrectPenaltyAndSituationTests
{
    private static AppDbContext NewDbContext(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName ?? $"correct-penalty-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private sealed record Harness(
        BookingCancellationService Service,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceMock,
        string DbName);

    private static Harness BuildService(bool flagOn = true, string? dbName = null)
    {
        dbName ??= $"correct-penalty-{Guid.NewGuid()}";
        var ctx = NewDbContext(dbName);
        var invoiceMock = new Mock<IInvoiceService>();
        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnableCancellationDebitNote = flagOn,
            EnableMultiCurrencyInvoicing = true,
            CancellationDebitNoteGraceDays = 15,
            CancellationDebitNoteHardWarnDays = 60,
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
        };
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

        return new Harness(service, ctx, invoiceMock, dbName);
    }

    /// <summary>
    /// Semilla post-NC (factura C=11 en PES con CAE + NC C con CAE), BC en AwaitingOperatorRefund. Devuelve las
    /// entidades para que cada test las lleve al estado que necesita.
    /// </summary>
    private static async Task<(Guid BcPublicId, BookingCancellation Bc, Supplier Supplier, Reserva Reserva)>
        SeedPostNcAsync(AppDbContext ctx, string? confirmedByUserId = null)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador X", IsActive = true, PenaltyOwnership = PenaltyOwnership.Operator };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-CORRECT",
            Name = "Reserva Test",
            PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund,
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11,
            PuntoDeVenta = 1,
            NumeroComprobante = 100,
            CAE = "12345678",
            Resultado = "A",
            MonId = "PES",
            MonCotiz = 1m,
            ImporteTotal = 100_000m,
            ImporteNeto = 100_000m,
            ImporteIva = 0m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        var creditNote = new Invoice
        {
            TipoComprobante = 13,
            PuntoDeVenta = 1,
            NumeroComprobante = 101,
            CAE = "99999999",
            Resultado = "A",
            ReservaId = reserva.Id,
            OriginalInvoiceId = original.Id,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id,
            CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cliente anulo",
            DraftedByUserId = "vendedor-1",
            ConfirmedByUserId = confirmedByUserId,
            ConfirmedByUserName = confirmedByUserId,
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-10),
            // ADR-044 T3a (2026-07-10): la agencia emisora (Monotributo, igual que el resto de la suite de ND)
            // congelada al confirmar la cancelacion. CorrectPenaltyAsync vuelve a pasar por
            // AllocateConfirmedPenaltyToLinesAsync (crea un cargo tipificado nuevo), y la ND multi-operador
            // necesita esta condicion fiscal para resolver la alicuota de IVA del renglon.
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = "ARS",
                AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent = "MONOTRIBUTISTA",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                FetchedAt = DateTime.UtcNow.AddDays(-10),
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc.PublicId, bc, supplier, reserva);
    }

    /// <summary>Deja el BC como "multa confirmada pass-through con ND trabada" (ManualReview, sin factura vinculada).</summary>
    private static async Task SeedConfirmedManualReviewAsync(
        Harness h, BookingCancellation bc, Supplier supplier,
        decimal penalty, string declaredCurrency)
    {
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.PenaltyAmountAtEvent = penalty;
        bc.PenaltyCurrencyAtEvent = declaredCurrency;
        bc.DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge;
        bc.DebitNoteStatus = DebitNoteStatus.ManualReview;
        bc.DebitNoteInvoiceId = null;
        bc.DebitNoteArcaErrorMessage = "La multa se cargó en una moneda distinta a la factura.";
        bc.PenaltyConfirmedByUserId = "u";
        bc.PenaltyConfirmedByUserName = "U";
        bc.PenaltyConfirmedAt = DateTime.UtcNow.AddDays(-1);
        // El gating de la ND exige el rastro del clasificador del concepto; sin esto rutea a revision manual.
        bc.ConceptClassifiedByUserId = "u";
        bc.ConceptClassifiedByUserName = "U";
        bc.ConceptClassifiedAt = DateTime.UtcNow.AddDays(-1);

        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = supplier.Id,
            Currency = Monedas.ARS,
            RefundCap = 100_000m - penalty,
            PenaltyAmount = penalty,
            // ADR-044 T2 Addendum: eje CAJA. Este fixture es el camino legacy simple (Fee+Retenida): coincide con
            // PenaltyAmount (misma regla del backfill T2c para confirmadas legacy).
            RetainedDeductionAmount = penalty,
            PenaltyStatus = PenaltyStatus.Confirmed,
            ReceivedRefundAmount = 0m,
            RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund,
        });
        await h.Ctx.SaveChangesAsync();
    }

    /// <summary>CreateAsync emite una ND en la BD InMemory y captura el userId con el que se la llamo.</summary>
    private static void SetupCreateCapturesUser(Harness h, Action<string?> captureUserId)
    {
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                captureUserId(uid);
                var reservaId = h.Ctx.Reservas.First().Id;
                var originalId = h.Ctx.Invoices.First(i => i.TipoComprobante == 11).Id;
                var nd = new Invoice
                {
                    TipoComprobante = 12,
                    PuntoDeVenta = 1,
                    NumeroComprobante = 200,
                    Resultado = "PENDING",
                    ReservaId = reservaId,
                    OriginalInvoiceId = originalId,
                };
                h.Ctx.Invoices.Add(nd);
                h.Ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });
    }

    // ============================================================
    // A4 — CorrectPenaltyAsync
    // ============================================================

    [Fact]
    public async Task Correct_HappyPath_UpdatesAmountCurrency_AndReEnqueuesDebitNote()
    {
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        // Multa confirmada declarada en USD sobre factura en PES -> quedo trabada en ManualReview.
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "USD");
        string? capturedUser = null;
        SetupCreateCapturesUser(h, u => capturedUser = u);

        // Se corrige a ARS (coincide con la factura) y a un monto nuevo -> ahora el gating pasa y re-emite la ND.
        await h.Service.CorrectPenaltyAsync(
            bcId, amount: 45_000m, currency: "ARS", reason: "El operador retuvo en pesos, no dólares.",
            "corrector", "Corrector", default, userCanClassifyAgencyPenalty: true);

        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(45_000m, after.PenaltyAmountAtEvent);
        Assert.Equal("ARS", after.PenaltyCurrencyAtEvent);
        Assert.Equal(PenaltyStatus.Confirmed, after.PenaltyStatus); // sigue confirmada, solo se corrigio
        Assert.Equal(DebitNoteStatus.Pending, after.DebitNoteStatus); // re-encolada
        Assert.NotNull(after.DebitNoteInvoiceId);
        Assert.Null(after.DebitNoteArcaErrorMessage); // A7: el error viejo se limpio
        // A3: la ND se atribuye al actor de la correccion.
        Assert.Equal("corrector", capturedUser);

        // Fix "aserción faltante en el happy path" (review 2026-07-08): la linea del operador quedo con la multa
        // NUEVA imputada, no la vieja. capBeforePenalty de la linea era 100_000 (RefundCap se restauro entero
        // antes de re-imputar), asi que con la multa nueva de 45_000 el RefundCap tiene que quedar en 55_000 —
        // se verifica la invariante RefundCap + PenaltyAmount == capBeforePenalty.
        var line = h.Ctx.BookingCancellationLines.Single();
        Assert.Equal(45_000m, line.PenaltyAmount);
        Assert.Equal(55_000m, line.RefundCap);
        Assert.Equal(100_000m, line.RefundCap + line.PenaltyAmount!.Value);
    }

    [Fact]
    public async Task Correct_WhenDebitNoteAlreadyIssuedWithCae_Rebounds409()
    {
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "ARS");
        // Ya hay una ND EMITIDA con CAE: no se puede corregir por este camino.
        bc.DebitNoteStatus = DebitNoteStatus.Issued;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 45_000m, "ARS", "Corregir igual", "corrector", "Corrector", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-CORRECT-002", ex.InvariantCode);
    }

    [Fact]
    public async Task Correct_WithInvalidCurrency_Throws400()
    {
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "ARS");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 45_000m, "EUR", "Moneda no soportada", "corrector", "Corrector", default, userCanClassifyAgencyPenalty: true));
    }

    [Fact]
    public async Task Correct_WithZeroAmount_Throws400()
    {
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "ARS");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 0m, "ARS", "Monto invalido", "corrector", "Corrector", default, userCanClassifyAgencyPenalty: true));
    }

    [Fact]
    public async Task Correct_WhenPenaltyNotConfirmed_Rebounds409()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);
        // Penalidad aun Estimated (recien sembrada): no hay nada que corregir.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 45_000m, "ARS", "Corregir una no confirmada", "corrector", "Corrector", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-CORRECT-001", ex.InvariantCode);
    }

    [Fact]
    public async Task Correct_WhenDebitNotePendingInFlight_Rebounds409()
    {
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "ARS");
        // Una ND encolada EN VUELO (Pending, link seteado, sin CAE): corregir en el medio seria una carrera.
        var nd = new Invoice
        {
            TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 300,
            Resultado = "PENDING", ReservaId = bc.ReservaId, OriginalInvoiceId = bc.OriginatingInvoiceId,
        };
        h.Ctx.Invoices.Add(nd);
        await h.Ctx.SaveChangesAsync();
        bc.DebitNoteInvoiceId = nd.Id;
        bc.DebitNoteStatus = DebitNoteStatus.Pending;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 45_000m, "ARS", "Corregir con ND en vuelo", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-CORRECT-003", ex.InvariantCode);
    }

    [Fact]
    public async Task Correct_WithoutPermission_Rebounds409()
    {
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "ARS");

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 45_000m, "ARS", "Sin permiso", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: false));
        Assert.Equal("INV-CORRECT-PERM", ex.InvariantCode);
    }

    [Fact]
    public async Task Correct_WhenWaivedByAnotherRequestDuringTheLock_Rebounds409_AndLeavesLinesIntact()
    {
        // Regresion (review 2026-07-08): carrera CorrectPenaltyAsync vs WaiveOperatorPenaltyAsync. El chequeo de
        // PenaltyStatus==Confirmed de CorrectPenaltyAsync se hacia SOLO fuera del lock; si otro admin cerraba sin
        // multa ("Waive") en el medio, el ReloadAsync de DENTRO del lock traia PenaltyStatus=Waived pero nada lo
        // volvia a chequear -> la correccion seguia de largo y le recortaba el RefundCap a una linea que el waive
        // ya habia restaurado (descuadre silencioso). El fix agrega un segundo chequeo DENTRO del lock, despues
        // del Reload.
        //
        // Como simular la carrera en InMemory (que no tiene lock real, RunUnderParentLockAsync corre el cuerpo
        // directo): EF Core, cuando una entidad YA esta trackeada en el ChangeTracker de un DbContext, NO pisa sus
        // valores en memoria con una consulta comun (solo un ReloadAsync explicito lo hace). Entonces: (1)
        // pre-cargamos el BC en el MISMO DbContext que usa el service (asi la consulta de AFUERA de
        // CorrectPenaltyAsync devuelve esa instancia YA trackeada, con la foto vieja "Confirmed"); (2) desde OTRO
        // DbContext apuntando a la MISMA base InMemory (como pasaria en produccion: otro request, otro scope) se
        // aplica el waive de verdad; (3) al llamar a CorrectPenaltyAsync, el chequeo de afuera pasa con la foto
        // vieja, pero el ReloadAsync de DENTRO del lock trae el estado real (Waived) y el guard nuevo lo frena.
        var h = BuildService(dbName: $"correct-penalty-race-{Guid.NewGuid()}");
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "ARS");

        // Pre-cargamos el BC (y su linea) en el ChangeTracker de h.Ctx, el mismo contexto que usa el service.
        var trackedBc = await h.Ctx.BookingCancellations.SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(PenaltyStatus.Confirmed, trackedBc.PenaltyStatus); // foto vieja que va a quedar "pisada"

        // Foto de la linea ANTES de intentar corregir (la multa original de 30_000, con su cap ya recortado a
        // 70_000). Es lo que tiene que seguir habiendo despues del rebote: la correccion frenada NO llega a tocar
        // la linea (el guard nuevo tira ANTES del paso que deshace/reimputa).
        var lineBefore = h.Ctx.BookingCancellationLines.Single();
        var refundCapBefore = lineBefore.RefundCap;
        var penaltyBefore = lineBefore.PenaltyAmount;

        // El waive corre "en paralelo", desde OTRO DbContext sobre la MISMA base InMemory (como pasaria en
        // produccion: cada request tiene su propio scope de DbContext). Le pega al BC (que h.Ctx SI vuelve a leer
        // via Reload dentro del lock, por eso el guard nuevo lo detecta) y a la linea (que h.Ctx NO vuelve a leer
        // en este camino, porque el guard frena antes de llegar al paso que toca lineas — asi que la linea de
        // h.Ctx se queda con su foto de ANTES, no con la que puso este otro contexto).
        using (var otherRequestCtx = NewDbContext(h.DbName))
        {
            var bcFromOtherRequest = await otherRequestCtx.BookingCancellations.SingleAsync(b => b.Id == bc.Id);
            var lineFromOtherRequest = await otherRequestCtx.BookingCancellationLines
                .SingleAsync(l => l.BookingCancellationId == bc.Id);

            bcFromOtherRequest.PenaltyStatus = PenaltyStatus.Waived;
            bcFromOtherRequest.PenaltyAmountAtEvent = 0m;
            bcFromOtherRequest.DebitNoteStatus = DebitNoteStatus.NotApplicable;
            bcFromOtherRequest.DebitNoteArcaErrorMessage = null;
            lineFromOtherRequest.RefundCap = 100_000m; // el waive restaura el cap entero (deshace la multa de 30_000).
            lineFromOtherRequest.PenaltyAmount = null;
            await otherRequestCtx.SaveChangesAsync();
        }

        // La correccion: el chequeo de afuera ve la foto vieja (Confirmed) y pasa, pero el Reload de adentro del
        // lock trae el estado real (Waived) -> debe rebotar con la MISMA invariante que el chequeo de afuera.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 45_000m, "ARS", "Corregir sobre una multa que se cerro sin multa en el medio",
                "corrector", "Corrector", default, userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-CORRECT-001", ex.InvariantCode);

        // La linea NO se toco: el guard frena ANTES de deshacer/reimputar nada. Si el fix no estuviera, la
        // correccion hubiera seguido de largo y hubiera dejado la linea con la multa NUEVA (RefundCap=55_000,
        // PenaltyAmount=45_000) sobre un BC que ya estaba Waived — el descuadre que el fix evita.
        var lineAfter = h.Ctx.BookingCancellationLines.Single();
        Assert.Equal(refundCapBefore, lineAfter.RefundCap);
        Assert.Equal(penaltyBefore, lineAfter.PenaltyAmount);
    }

    // ============================================================
    // A3 — atribucion del retry al actor real
    // ============================================================

    [Fact]
    public async Task Retry_AttributesDebitNoteToRetryActor_NotToAnnulmentConfirmer()
    {
        var h = BuildService();
        // La ANULACION la confirmo "anulador"; el retry lo dispara "retry-user". La ND debe quedar a nombre del retry.
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx, confirmedByUserId: "anulador");
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "ARS");
        string? capturedUser = null;
        SetupCreateCapturesUser(h, u => capturedUser = u);

        await h.Service.RetryDebitNoteEmissionAsync(
            bcId, "retry-user", "Retry User", default, userCanClassifyAgencyPenalty: true);

        Assert.Equal("retry-user", capturedUser);
        Assert.NotEqual("anulador", capturedUser);
    }

    [Fact]
    public async Task Retry_OverFailedDebitNote_DropsDeadLink_AndEmitsOneNewDebitNote()
    {
        // Funcional B1 (2026-07-08): una ND FALLIDA (rechazada por AFIP, sin CAE) tiene el link seteado. El retry
        // debe SOLTAR ese link muerto y emitir una NUEVA (sin re-vincular la rechazada, sin duplicar).
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "ARS");

        // ND RECHAZADA vinculada (Resultado="R", sin CAE) + DebitNoteStatus=Failed.
        var rejected = new Invoice
        {
            TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 400,
            Resultado = "R", CAE = null, ReservaId = bc.ReservaId, OriginalInvoiceId = bc.OriginatingInvoiceId,
        };
        h.Ctx.Invoices.Add(rejected);
        await h.Ctx.SaveChangesAsync();
        bc.DebitNoteInvoiceId = rejected.Id;
        bc.DebitNoteStatus = DebitNoteStatus.Failed;
        bc.DebitNoteArcaErrorMessage = "AFIP rechazó la ND.";
        await h.Ctx.SaveChangesAsync();

        var createCalls = 0;
        SetupCreateCapturesUser(h, _ => createCalls++);

        await h.Service.RetryDebitNoteEmissionAsync(
            bcId, "retry-user", "Retry User", default, userCanClassifyAgencyPenalty: true);

        var after = h.Ctx.BookingCancellations.Single();
        // Se emitio UNA nueva ND (no se re-vinculo la rechazada) y quedo encolada.
        Assert.Equal(1, createCalls);
        Assert.Equal(DebitNoteStatus.Pending, after.DebitNoteStatus);
        Assert.NotNull(after.DebitNoteInvoiceId);
        Assert.NotEqual(rejected.Id, after.DebitNoteInvoiceId); // NO es la rechazada
        Assert.Null(after.DebitNoteArcaErrorMessage);           // A7: error viejo limpiado
    }

    // ============================================================
    // A6 — freno de consistencia en revert-waive (saldo a favor ya usado)
    // ============================================================

    /// <summary>Cierra sin multa el BC (Estimated -> Waived) para poder despues intentar reabrirlo.</summary>
    private static async Task WaiveAsync(Harness h, Guid bcId)
        => await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "El operador no cobra multa.", "u", "U", default, userCanClassifyAgencyPenalty: true);

    private static void SeedClientCredit(Harness h, BookingCancellation bc, Customer_LikeIds ids, decimal remaining)
    {
        h.Ctx.ClientCreditEntries.Add(new ClientCreditEntry
        {
            CustomerId = ids.CustomerId,
            BookingCancellationId = bc.Id,
            Currency = Monedas.ARS,
            CreditedAmount = 50_000m,
            RemainingBalance = remaining,
            IsFullyConsumed = remaining <= 0m,
        });
        h.Ctx.SaveChanges();
    }

    private readonly record struct Customer_LikeIds(int CustomerId);

    [Fact]
    public async Task Revert_WhenClientCreditFullyUsed_Rebounds409_SaldoYaUsado()
    {
        var h = BuildService();
        var (bcId, bc, _, _) = await SeedPostNcAsync(h.Ctx);
        await WaiveAsync(h, bcId);

        // Esta anulacion habia generado saldo a favor del cliente, pero ya se consumio TODO (RemainingBalance = 0).
        SeedClientCredit(h, bc, new Customer_LikeIds(bc.CustomerId), remaining: 0m);

        var ex = await Assert.ThrowsAsync<ClientCreditAlreadyUsedException>(() =>
            h.Service.RevertWaivedOperatorPenaltyAsync(
                bcId, "El operador cobro una multa tardia.", "admin", "Admin", requesterIsAdmin: true, default));
        Assert.Contains("saldo a favor", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SALDO_YA_USADO", ClientCreditAlreadyUsedException.ErrorCode);

        // El estado NO cambio: sigue Waived.
        Assert.Equal(PenaltyStatus.Waived, h.Ctx.BookingCancellations.Single().PenaltyStatus);
    }

    [Fact]
    public async Task Revert_WhenClientCreditStillAvailable_Succeeds()
    {
        var h = BuildService();
        var (bcId, bc, _, _) = await SeedPostNcAsync(h.Ctx);
        await WaiveAsync(h, bcId);

        // Todavia queda saldo a favor disponible -> se permite reabrir.
        SeedClientCredit(h, bc, new Customer_LikeIds(bc.CustomerId), remaining: 20_000m);

        await h.Service.RevertWaivedOperatorPenaltyAsync(
            bcId, "El operador cobro una multa tardia.", "admin", "Admin", requesterIsAdmin: true, default);

        Assert.Equal(PenaltyStatus.Estimated, h.Ctx.BookingCancellations.Single().PenaltyStatus);
    }

    [Fact]
    public async Task Revert_WhenNoClientCreditEverExisted_Succeeds()
    {
        var h = BuildService();
        var (bcId, _, _, _) = await SeedPostNcAsync(h.Ctx);
        await WaiveAsync(h, bcId);

        // Esta anulacion NUNCA genero saldo a favor (multa pura): el freno NO debe sobre-bloquear.
        await h.Service.RevertWaivedOperatorPenaltyAsync(
            bcId, "Reabrir para cargar la multa.", "admin", "Admin", requesterIsAdmin: true, default);

        Assert.Equal(PenaltyStatus.Estimated, h.Ctx.BookingCancellations.Single().PenaltyStatus);
    }

    // ============================================================
    // A2 — GetOperatorPenaltySituationAsync (read-model del paso)
    // ============================================================

    [Fact]
    public async Task Situation_WhenPending_IsPendingDecision_WithConfirmEnabled()
    {
        var h = BuildService();
        var (_, _, _, reserva) = await SeedPostNcAsync(h.Ctx);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.PendingDecision.ToString(), sit.State);
        Assert.True(sit.CanConfirm);
        Assert.False(sit.CanRetryDebitNote);
        Assert.False(sit.CanCorrectAmountCurrency);
        // Pendiente de decidir todavia no es "Confirmed" -> el cierre sin multa de ESTE boton no aplica aca aunque
        // el caller sea Admin (ese caso ya lo cubre el flujo confirmar/cerrar-sin-multa desde pendiente, no CanWaive).
        Assert.False(sit.CanWaive);
    }

    [Fact]
    public async Task Situation_WhenPending_ButNoPermission_CanConfirmFalse()
    {
        var h = BuildService();
        var (_, _, _, reserva) = await SeedPostNcAsync(h.Ctx);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: false, isCallerAdmin: false, default);

        Assert.Equal(OperatorPenaltySituationState.PendingDecision.ToString(), sit.State);
        Assert.False(sit.CanConfirm); // el estado permite, pero sin permiso no se ofrece el boton
    }

    [Fact]
    public async Task Situation_WhenManualReview_IsNeedsAmountCurrency_WithCorrectEnabled()
    {
        var h = BuildService();
        var (_, bc, supplier, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "USD");

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency.ToString(), sit.State);
        Assert.True(sit.CanCorrectAmountCurrency);
        Assert.False(sit.CanConfirm);
        Assert.Equal(30_000m, sit.Amount);
        Assert.Equal("USD", sit.Currency); // ISO para mostrar
        // Confirmed + ND trabada en revision manual (sin factura vinculada) + caller Admin -> se puede cerrar sin multa.
        Assert.True(sit.CanWaive);
    }

    [Fact]
    public async Task Situation_WhenManualReview_PermissionButNotAdmin_CanWaiveFalse()
    {
        // Anti-patron "boton que rebota" (review 2026-07-08): cerrar sin multa una multa YA confirmada exige rol
        // ADMIN (INV-WAIVE-005), no basta el permiso classify. Un no-admin CON el permiso classify puede confirmar/
        // corregir/reintentar, pero NO debe ver el boton "cerrar sin multa": si lo viera, al apretarlo rebotaria 409.
        var h = BuildService();
        var (_, bc, supplier, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "USD");

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: false, default);

        // Corregir SI (tiene el permiso classify), pero cerrar sin multa NO (no es Admin).
        Assert.True(sit.CanCorrectAmountCurrency);
        Assert.False(sit.CanWaive);
    }

    /// <summary>Lleva el BC a Confirmed con un DebitNoteStatus dado (y opcionalmente una ND vinculada), para cubrir la matriz.</summary>
    private static async Task SeedConfirmedStateAsync(
        Harness h, BookingCancellation bc, DebitNoteStatus debitNote, bool withLinkedInvoice)
    {
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.PenaltyAmountAtEvent = 30_000m;
        bc.PenaltyCurrencyAtEvent = "ARS";
        bc.DebitNoteStatus = debitNote;
        bc.PenaltyConfirmedByUserId = "u";
        bc.PenaltyConfirmedByUserName = "U";
        bc.PenaltyConfirmedAt = DateTime.UtcNow.AddDays(-1);
        if (withLinkedInvoice)
        {
            var nd = new Invoice
            {
                TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 500,
                Resultado = debitNote == DebitNoteStatus.Issued ? "A" : "PENDING",
                CAE = debitNote == DebitNoteStatus.Issued ? "77777777" : null,
                ReservaId = bc.ReservaId, OriginalInvoiceId = bc.OriginatingInvoiceId,
            };
            h.Ctx.Invoices.Add(nd);
            await h.Ctx.SaveChangesAsync();
            bc.DebitNoteInvoiceId = nd.Id;
        }
        await h.Ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Situation_WhenDebitNoteFailed_IsFailed_WithRetryAndCorrectEnabled()
    {
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedStateAsync(h, bc, DebitNoteStatus.Failed, withLinkedInvoice: false);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteFailed.ToString(), sit.State);
        Assert.True(sit.CanRetryDebitNote);
        Assert.True(sit.CanCorrectAmountCurrency);
        Assert.False(sit.CanConfirm);
        // Failed SIN factura vinculada (el link ya se solto) + caller Admin -> se puede cerrar sin multa.
        Assert.True(sit.CanWaive);
    }

    [Fact]
    public async Task Situation_WhenDebitNoteFailed_ButStillLinkedToDeadInvoice_CanWaiveFalse()
    {
        // Caso "ND en juego" distinto al de arriba: una Failed que TODAVIA tiene la factura muerta vinculada
        // (asi queda justo despues de que ProcessInvoiceJob reconcilia el rechazo de ARCA, antes de que alguien
        // toque "Reintentar" y suelte el link). Mientras el link exista, cerrar sin multa dejaria una ND
        // rechazada colgada sin resolver -> se bloquea, misma condicion que WaiveOperatorPenaltyAsync.
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedStateAsync(h, bc, DebitNoteStatus.Failed, withLinkedInvoice: true);

        // Admin, para probar que el false es por la ND-en-juego y NO por falta de rol.
        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteFailed.ToString(), sit.State);
        Assert.False(sit.CanWaive);
    }

    [Fact]
    public async Task Situation_WhenConfirmedNoDebitNote_IsConfirmedNoDebitNote_WithRetryEnabled()
    {
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedStateAsync(h, bc, DebitNoteStatus.NotApplicable, withLinkedInvoice: false);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.ConfirmedNoDebitNote.ToString(), sit.State);
        Assert.True(sit.CanRetryDebitNote);
        Assert.False(sit.CanCorrectAmountCurrency);
        Assert.True(sit.CanWaive);
    }

    [Fact]
    public async Task Situation_WhenDebitNoteQueued_IsQueued_WithNoActions()
    {
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedStateAsync(h, bc, DebitNoteStatus.Pending, withLinkedInvoice: true);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteQueued.ToString(), sit.State);
        Assert.False(sit.CanConfirm);
        Assert.False(sit.CanRetryDebitNote);
        Assert.False(sit.CanCorrectAmountCurrency);
        // ND encolada (Pending) es una ND EN JUEGO -> nunca se ofrece cerrar sin multa, ni siquiera al Admin.
        Assert.False(sit.CanWaive);
    }

    [Fact]
    public async Task Situation_WhenDebitNoteIssued_IsDone_WithNoActions()
    {
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedStateAsync(h, bc, DebitNoteStatus.Issued, withLinkedInvoice: true);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.Done.ToString(), sit.State);
        Assert.False(sit.CanConfirm);
        Assert.False(sit.CanRetryDebitNote);
        Assert.False(sit.CanCorrectAmountCurrency);
        // ND ya emitida con CAE -> la pata quedo resuelta, nunca se ofrece cerrar sin multa, ni siquiera al Admin.
        Assert.False(sit.CanWaive);
    }

    [Fact]
    public async Task Situation_AfterWaive_IsWaived_WithWaivedByName()
    {
        var h = BuildService();
        var (bcId, _, _, reserva) = await SeedPostNcAsync(h.Ctx);
        await h.Service.WaiveOperatorPenaltyAsync(
            bcId, "El operador no cobra multa.", "cerrador", "Cerrador", default, userCanClassifyAgencyPenalty: true);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.Waived.ToString(), sit.State);
        Assert.Equal("Cerrador", sit.WaivedByName);
        Assert.NotNull(sit.WaivedAt);
        Assert.Null(sit.Amount); // "no hubo multa": no se muestra $0
        Assert.False(sit.CanConfirm);
        // Ya se cerro sin multa (PenaltyStatus=Waived, no Confirmed) -> no se vuelve a ofrecer el boton.
        Assert.False(sit.CanWaive);
        // GAP conocido: el rastro de reversa no se persiste -> siempre null.
        Assert.Null(sit.RevertedAt);
        Assert.Null(sit.RevertedByName);
    }

    [Fact]
    public async Task Situation_WhenNoCancellation_IsNone()
    {
        var h = BuildService();
        var reserva = new Reserva
        {
            NumeroReserva = "R-NONE",
            Name = "Sin cancelacion",
            Status = EstadoReserva.Confirmed,
            Balance = 0m,
        };
        h.Ctx.Reservas.Add(reserva);
        await h.Ctx.SaveChangesAsync();

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.None.ToString(), sit.State);
        Assert.False(sit.CanConfirm);
        Assert.False(sit.CanWaive);
        Assert.Null(sit.Amount);
        Assert.Null(sit.Since);
    }
}
