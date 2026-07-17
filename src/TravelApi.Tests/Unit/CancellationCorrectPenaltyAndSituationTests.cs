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

    // ============================================================
    // TANDA C "la multa cobrada se ve cerrada" (2026-07-16): IsFullyCollected/FullyCollectedAt del read-model.
    // ANTES el cartel solo miraba si la ND tenia CAE (estado "Done"), nunca si el cliente ya la habia pagado.
    // ============================================================

    /// <summary>Lleva el BC a Confirmed con una ND EMITIDA (CAE) de un monto real, para probar la cuenta ND-BASED.</summary>
    private static async Task<int> SeedIssuedDebitNoteWithAmountAsync(
        Harness h, BookingCancellation bc, decimal importeTotal, string monId = "PES")
    {
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.PenaltyAmountAtEvent = importeTotal;
        bc.PenaltyCurrencyAtEvent = monId;
        bc.DebitNoteStatus = DebitNoteStatus.Issued;
        bc.PenaltyConfirmedByUserId = "u";
        bc.PenaltyConfirmedByUserName = "U";
        bc.PenaltyConfirmedAt = DateTime.UtcNow.AddDays(-1);

        var nd = new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = 500,
            Resultado = "A",
            CAE = "77777777",
            ReservaId = bc.ReservaId,
            ImporteTotal = importeTotal,
            MonId = monId,
        };
        h.Ctx.Invoices.Add(nd);
        await h.Ctx.SaveChangesAsync();

        bc.DebitNoteInvoiceId = nd.Id;
        await h.Ctx.SaveChangesAsync();
        return nd.Id;
    }

    private static async Task SeedPaymentLinkedToDebitNoteAsync(
        Harness h, int debitNoteId, decimal amount, DateTime createdAt,
        string status = "Paid", bool isDeleted = false)
    {
        h.Ctx.Payments.Add(new Payment
        {
            LinkedInvoiceId = debitNoteId,
            Amount = amount,
            Status = status,
            IsDeleted = isDeleted,
            CreatedAt = createdAt,
            AffectsReservaBalance = false, // fiel al comportamiento real de un cobro de ND en una anulada.
        });
        await h.Ctx.SaveChangesAsync();
    }

    private static async Task SeedCreditNoteAssociatedToDebitNoteAsync(Harness h, int debitNoteId, decimal amount)
    {
        h.Ctx.Invoices.Add(new Invoice
        {
            TipoComprobante = 13, // NC C
            PuntoDeVenta = 1,
            NumeroComprobante = 600,
            Resultado = "A",
            CAE = "88888888",
            ImporteTotal = amount,
            OriginalInvoiceId = debitNoteId,
            AnnulmentStatus = AnnulmentStatus.None,
        });
        await h.Ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task FullyCollected_WhenIssuedWithNoPayments_IsFalse()
    {
        // (a) multa emitida sin cobros: nada se pago todavia -> IsFullyCollected false.
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedIssuedDebitNoteWithAmountAsync(h, bc, importeTotal: 1000m);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.False(sit.IsFullyCollected);
        Assert.Null(sit.FullyCollectedAt);
    }

    [Fact]
    public async Task FullyCollected_WhenPartiallyPaid_IsFalse()
    {
        // (b) cobro parcial: pago 400 de una ND de 1000 -> todavia queda pendiente -> IsFullyCollected false.
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        var debitNoteId = await SeedIssuedDebitNoteWithAmountAsync(h, bc, importeTotal: 1000m);
        await SeedPaymentLinkedToDebitNoteAsync(h, debitNoteId, amount: 400m, createdAt: DateTime.UtcNow.AddDays(-1));

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.False(sit.IsFullyCollected);
        Assert.Null(sit.FullyCollectedAt);
    }

    [Fact]
    public async Task FullyCollected_WhenFullyPaid_IsTrue_WithLastPaymentDate()
    {
        // (c) cobro total en DOS pagos: el cartel debe pasar a "cobrada" con la fecha del pago MAS RECIENTE.
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        var debitNoteId = await SeedIssuedDebitNoteWithAmountAsync(h, bc, importeTotal: 1000m);
        var olderPaymentAt = DateTime.UtcNow.AddDays(-3);
        var mostRecentPaymentAt = DateTime.UtcNow.AddDays(-1);
        await SeedPaymentLinkedToDebitNoteAsync(h, debitNoteId, amount: 600m, createdAt: olderPaymentAt);
        await SeedPaymentLinkedToDebitNoteAsync(h, debitNoteId, amount: 400m, createdAt: mostRecentPaymentAt);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.True(sit.IsFullyCollected);
        Assert.Equal(mostRecentPaymentAt, sit.FullyCollectedAt);
    }

    [Fact]
    public async Task FullyCollected_IgnoresCancelledAndDeletedPayments()
    {
        // (d) un pago CANCELADO o BORRADO no cuenta como cobrado: la ND de 1000 con un pago de 1000 pero
        // Cancelled sigue mostrando "pendiente", no "cobrada".
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        var debitNoteId = await SeedIssuedDebitNoteWithAmountAsync(h, bc, importeTotal: 1000m);
        await SeedPaymentLinkedToDebitNoteAsync(
            h, debitNoteId, amount: 1000m, createdAt: DateTime.UtcNow.AddDays(-1), status: "Cancelled");
        await SeedPaymentLinkedToDebitNoteAsync(
            h, debitNoteId, amount: 1000m, createdAt: DateTime.UtcNow.AddDays(-1), isDeleted: true);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.False(sit.IsFullyCollected);
        Assert.Null(sit.FullyCollectedAt);
    }

    [Fact]
    public async Task FullyCollected_CreditNoteReducesOutstanding()
    {
        // (e) una Nota de Credito asociada a la ND (por ejemplo, se anulo parcialmente la multa) reduce el
        // pendiente igual que un cobro: NC de 1000 sobre una ND de 1000, sin ningun pago -> queda cobrada
        // (nada por cobrar), pero sin fecha de pago (no hubo ningun Payment vivo vinculado).
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        var debitNoteId = await SeedIssuedDebitNoteWithAmountAsync(h, bc, importeTotal: 1000m);
        await SeedCreditNoteAssociatedToDebitNoteAsync(h, debitNoteId, amount: 1000m);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.True(sit.IsFullyCollected);
        Assert.Null(sit.FullyCollectedAt); // cerrada por NC, no por un pago: no hay fecha de pago que mostrar.
    }

    [Theory]
    [InlineData(DebitNoteStatus.Failed)]
    [InlineData(DebitNoteStatus.Pending)]
    [InlineData(DebitNoteStatus.ManualReview)]
    [InlineData(DebitNoteStatus.NotApplicable)]
    public async Task FullyCollected_WhenDebitNoteHasNoCae_IsAlwaysFalse(DebitNoteStatus debitNoteStatus)
    {
        // (f) multa sin CAE (encolada, fallida, en revision manual, o confirmada-sin-ND): sin comprobante
        // emitido no hay nada que pueda estar "cobrado" -> siempre false, sin importar los datos.
        var h = BuildService();
        var (_, bc, _, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedStateAsync(h, bc, debitNoteStatus, withLinkedInvoice: debitNoteStatus == DebitNoteStatus.Pending);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.False(sit.IsFullyCollected);
        Assert.Null(sit.FullyCollectedAt);
    }

    // ============================================================
    // ADR-044 T3b/T4 (2026-07-10, fix data-exposure): ManualReviewReason NUNCA porta el DebitNoteArcaErrorMessage
    // crudo. Solo viaja el mensaje LIMPIO fijo cuando el motivo es el caso derivable "falta elegir la factura".
    // ============================================================

    [Fact]
    public async Task Situation_ManualReview_ByCurrencyMismatch_ManualReviewReasonNull()
    {
        // Motivo "moneda distinta a la factura": 1 sola factura activa, sin cargo trasladable sin resolver. El
        // DebitNoteArcaErrorMessage guardado es un texto de negocio, pero NO es el caso "falta elegir factura",
        // asi que ManualReviewReason viaja NULL (el front muestra su copy fija de "corregir monto y moneda").
        var h = BuildService();
        var (_, bc, supplier, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "USD");

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency.ToString(), sit.State);
        Assert.Null(sit.ManualReviewReason);
    }

    [Fact]
    public async Task Situation_ManualReview_WithTechnicalArcaMessage_AndResolvedTargetInvoice_ManualReviewReasonNull()
    {
        // Blindaje data-exposure: aunque el DebitNoteArcaErrorMessage guardado sea texto TECNICO en español que
        // la blocklist de saneo NO ataja ("OriginatingInvoice no cargada."), como NO es el caso "falta elegir
        // factura" (el cargo YA tiene su factura destino resuelta, con 2 facturas activas), ManualReviewReason
        // viaja NULL: ese string interno nunca llega al usuario.
        var h = BuildService();
        var (_, _, _, reserva) = await SeedManualReviewWithChargeAsync(
            h, addSecondActiveInvoice: true, chargeTargetResolved: true,
            storedArcaMessage: "OriginatingInvoice no cargada.");

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency.ToString(), sit.State);
        Assert.Null(sit.ManualReviewReason);
    }

    [Fact]
    public async Task Situation_ManualReview_TargetInvoiceUnchosen_ManualReviewReasonIsCleanFixedMessage()
    {
        // Caso derivable "falta elegir la factura": 2 facturas activas + un cargo trasladable SIN factura destino.
        // El DebitNoteArcaErrorMessage guardado se pone a un texto TECNICO a proposito: el read-model NO lo copia,
        // reconstruye la condicion en vivo y expone el mensaje LIMPIO conocido (prueba de que no depende del
        // string interno).
        var h = BuildService();
        var (_, _, _, reserva) = await SeedManualReviewWithChargeAsync(
            h, addSecondActiveInvoice: true, chargeTargetResolved: false,
            storedArcaMessage: "fail-safe: coleccion no cargada (M2).");

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency.ToString(), sit.State);
        Assert.Equal(BookingCancellationService.TargetInvoiceUnchosenManualReviewMessage, sit.ManualReviewReason);
        // No filtra el string interno guardado.
        Assert.DoesNotContain("fail-safe", sit.ManualReviewReason!);
        Assert.DoesNotContain("M2", sit.ManualReviewReason!);
    }

    /// <summary>
    /// Semilla del estado "multa confirmada + ND en revision manual" con UN cargo del operador. Controla si hay
    /// una segunda factura de venta activa y si el cargo tiene su factura destino resuelta, para cubrir los dos
    /// caminos de ManualReviewReason (null vs. mensaje "falta elegir factura").
    /// </summary>
    private static async Task<(Guid BcPublicId, BookingCancellation Bc, Supplier Supplier, Reserva Reserva)>
        SeedManualReviewWithChargeAsync(
            Harness h, bool addSecondActiveInvoice, bool chargeTargetResolved, string storedArcaMessage)
    {
        var (bcPublicId, seededBc, seededSupplier, seededReserva) = await SeedPostNcAsync(h.Ctx);

        // Segunda factura de venta activa (con CAE), para llegar a "2+ facturas activas".
        if (addSecondActiveInvoice)
        {
            h.Ctx.Invoices.Add(new Invoice
            {
                TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 305, CAE = "second-cae",
                Resultado = "A", MonId = "DOL", MonCotiz = 1000m, ImporteTotal = 300m,
                ReservaId = seededReserva.Id, AnnulmentStatus = AnnulmentStatus.None,
            });
            await h.Ctx.SaveChangesAsync();
        }

        seededBc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        seededBc.PenaltyStatus = PenaltyStatus.Confirmed;
        seededBc.PenaltyAmountAtEvent = 20_000m;
        seededBc.PenaltyCurrencyAtEvent = "ARS";
        seededBc.DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge;
        seededBc.DebitNoteStatus = DebitNoteStatus.ManualReview;
        seededBc.DebitNoteInvoiceId = null;
        seededBc.DebitNoteArcaErrorMessage = storedArcaMessage;
        seededBc.PenaltyConfirmedByUserId = "u";
        seededBc.PenaltyConfirmedByUserName = "U";
        seededBc.PenaltyConfirmedAt = DateTime.UtcNow.AddDays(-1);
        seededBc.ConceptClassifiedByUserId = "u";
        seededBc.ConceptClassifiedByUserName = "U";
        seededBc.ConceptClassifiedAt = DateTime.UtcNow.AddDays(-1);
        await h.Ctx.SaveChangesAsync();

        var line = new BookingCancellationLine
        {
            BookingCancellationId = seededBc.Id,
            SupplierId = seededSupplier.Id,
            Currency = Monedas.ARS,
            RefundCap = 80_000m,
            PenaltyAmount = 20_000m,
            RetainedDeductionAmount = 20_000m,
            PenaltyStatus = PenaltyStatus.Confirmed,
            RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund,
        };
        h.Ctx.BookingCancellationLines.Add(line);
        await h.Ctx.SaveChangesAsync();

        h.Ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = line.Id,
            Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = PenaltyCollectionMode.Retenida,
            Amount = 20_000m,
            Currency = Monedas.ARS,
            ClientTransferMode = ClientTransferMode.AsIs,
            // Resuelta -> a la 1ra factura activa (la original del BC). Sin resolver -> null (caso "falta elegir").
            TargetInvoiceId = chargeTargetResolved ? seededBc.OriginatingInvoiceId : (int?)null,
            ConfirmedByUserId = "u",
        });
        await h.Ctx.SaveChangesAsync();

        return (bcPublicId, seededBc, seededSupplier, seededReserva);
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

    // ============================================================
    // ADR-044 Fix B (2026-07-13): CorrectPenaltyAsync convierte una multa declarada en una moneda distinta a la de
    // la factura (Caso A: multa en USD, factura + lineas del operador en pesos), con TC provisto por el usuario y
    // auditado. Caso B (linea del operador en otra moneda) -> revision manual, sin convertir. El guard de coherencia
    // de la emision queda intacto: es imposible emitir un comprobante en la escala equivocada.
    // ============================================================

    /// <summary>Captura la CreateInvoiceRequest con la que se emite la ND, para poder afirmar moneda y renglones.</summary>
    private static void SetupCreateCapturesRequest(Harness h, Action<CreateInvoiceRequest> captureRequest)
    {
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                captureRequest(req);
                var reservaId = h.Ctx.Reservas.First().Id;
                var originalId = h.Ctx.Invoices.First(i => i.TipoComprobante == 11).Id;
                var nd = new Invoice
                {
                    TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 210,
                    Resultado = "PENDING", ReservaId = reservaId, OriginalInvoiceId = originalId,
                };
                h.Ctx.Invoices.Add(nd);
                h.Ctx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });
    }

    private static readonly DateTime OperatorChargedDate = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Correct_CaseA_UsdOnArsInvoice_ConvertsToArs_PersistsOriginal_AndEmitsSingleArsLine()
    {
        // Caso A (F-2026-1033): multa declarada USD 60, linea del operador en ARS, factura en pesos. Con TC 1000
        // el servidor convierte a ARS 60.000 ANTES de guardar; el gating pasa (ARS==ARS) y la ND se re-encola.
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");
        CreateInvoiceRequest? capturedRequest = null;
        SetupCreateCapturesRequest(h, r => capturedRequest = r);

        await h.Service.CorrectPenaltyAsync(
            bcId, amount: 60m, currency: "USD", reason: "El operador retuvo US$ 60.",
            "corrector", "Corrector", default, userCanClassifyAgencyPenalty: true,
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.BNA_VendedorDivisa,
            exchangeRateDate: OperatorChargedDate);

        var after = h.Ctx.BookingCancellations.Single();
        // La multa quedo en la moneda de la factura, por el monto convertido.
        Assert.Equal("ARS", after.PenaltyCurrencyAtEvent);
        Assert.Equal(60_000m, after.PenaltyAmountAtEvent);
        Assert.Equal(DebitNoteStatus.Pending, after.DebitNoteStatus); // re-encolada: el gating paso
        Assert.NotNull(after.DebitNoteInvoiceId);
        // El original declarado + el TC quedan en columnas ESTRUCTURADAS (M2), no solo en el JSON de auditoria.
        Assert.Equal(60m, after.DeclaredPenaltyOriginalAmount);
        Assert.Equal("USD", after.DeclaredPenaltyOriginalCurrency);
        Assert.Equal(1000m, after.PenaltyConversionExchangeRate);
        Assert.Equal(ExchangeRateSource.BNA_VendedorDivisa, after.PenaltyConversionExchangeRateSource);
        Assert.Equal(OperatorChargedDate, after.PenaltyConversionExchangeRateAt);

        // Emision por la RAMA de cargos tipificados T3b (allCharges>0), no LegacySingleItem: un unico renglon en
        // pesos y el comprobante hereda MonId=PES de la factura original.
        Assert.NotNull(capturedRequest);
        Assert.Equal("PES", capturedRequest!.MonId);
        Assert.Single(capturedRequest.Items);
        Assert.Equal(60_000m, capturedRequest.Items[0].Total);

        // El cargo del operador nace en la moneda de la LINEA (ARS), coherente con su cuenta.
        var charge = h.Ctx.BookingCancellationLineOperatorCharges.Single();
        Assert.Equal("ARS", Monedas.Normalizar(charge.Currency));
        Assert.Equal(60_000m, charge.Amount);
    }

    [Fact]
    public async Task Correct_CaseA_MoneyInvariant_HoldsInArs()
    {
        // Invariante de plata (Caso A): tras convertir y re-imputar, RefundCap + PenaltyAmount == capBeforePenalty
        // (100.000), con la retencion en ARS (moneda de la linea).
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");
        SetupCreateCapturesUser(h, _ => { });

        await h.Service.CorrectPenaltyAsync(
            bcId, 60m, "USD", "Convertir a pesos", "corrector", "Corrector", default,
            userCanClassifyAgencyPenalty: true,
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.BNA_VendedorDivisa,
            exchangeRateDate: OperatorChargedDate);

        var line = h.Ctx.BookingCancellationLines.Single();
        Assert.Equal(60_000m, line.PenaltyAmount);      // 60 USD * 1000 = 60.000 ARS
        Assert.Equal(40_000m, line.RefundCap);          // 100.000 - 60.000
        Assert.Equal(100_000m, line.RefundCap + line.PenaltyAmount!.Value);
        Assert.Equal(60_000m, line.RetainedDeductionAmount); // eje CAJA en ARS
        Assert.Equal("ARS", Monedas.Normalizar(line.PenaltyCurrency));
    }

    [Fact]
    public async Task Correct_CaseB_OperatorLineInOtherCurrency_RoutesToManualReview_WithoutMutating()
    {
        // Caso B: el operador retuvo USD sobre un servicio en USD, pero el cliente esta facturado en pesos. Un solo
        // TC no puede resolver a la vez el renglon ARS de la ND y el cap USD del operador -> revision manual, sin
        // convertir ni tocar nada. Idempotente (el BC queda como estaba).
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedCrossCurrencyOperatorLineAsync(h, bc, supplier, declaredUsd: 200m);

        var lineBefore = h.Ctx.BookingCancellationLines.Single();
        var refundCapBefore = lineBefore.RefundCap;
        var penaltyAmountBefore = lineBefore.PenaltyAmount;

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 200m, "USD", "El operador retuvo US$ 200", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: true,
                exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.BNA_VendedorDivisa,
                exchangeRateDate: OperatorChargedDate));
        Assert.Equal("INV-CORRECT-CROSSCURRENCY", ex.InvariantCode);
        // Mensaje al usuario sin jerga (data-exposure): habla de "una persona", no de IDs/enums.
        Assert.Contains("revisar una persona", ex.Message, StringComparison.OrdinalIgnoreCase);

        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(200m, after.PenaltyAmountAtEvent);       // no se re-grabo
        Assert.Equal("USD", after.PenaltyCurrencyAtEvent);     // no se convirtio
        Assert.Null(after.DeclaredPenaltyOriginalAmount);      // no se estampo la conversion
        Assert.Null(after.PenaltyConversionExchangeRate);
        Assert.Empty(h.Ctx.BookingCancellationLineOperatorCharges); // no se creo cargo
        var lineAfter = h.Ctx.BookingCancellationLines.Single();
        Assert.Equal(refundCapBefore, lineAfter.RefundCap);    // no se toco el RefundCap
        Assert.Equal(penaltyAmountBefore, lineAfter.PenaltyAmount);
    }

    [Fact]
    public async Task Correct_CaseA_WithUnreliableExchangeRate_Rejects400_WithoutMutating()
    {
        // Banda de sanidad: un TC <= 0 o == 1 es el "default peligroso" (cotizacion sin cargar). Se rechaza con 400
        // y NO se persiste nada.
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 60m, "USD", "TC invalido", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: true,
                exchangeRate: 1m, exchangeRateSource: (int)ExchangeRateSource.BNA_VendedorDivisa,
                exchangeRateDate: OperatorChargedDate));

        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal("USD", after.PenaltyCurrencyAtEvent); // intacto
        Assert.Equal(60m, after.PenaltyAmountAtEvent);
        Assert.Null(after.DeclaredPenaltyOriginalAmount);
    }

    [Fact]
    public async Task Correct_CaseA_WithoutExchangeRate_Rejects400_WithoutMutating()
    {
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");

        // Cruce de moneda pero SIN tipo de cambio: 400 con mensaje claro, no persiste.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 60m, "USD", "Sin TC", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: true,
                exchangeRate: null, exchangeRateSource: null, exchangeRateDate: OperatorChargedDate));

        Assert.Equal("USD", h.Ctx.BookingCancellations.Single().PenaltyCurrencyAtEvent);
    }

    [Fact]
    public async Task Correct_CaseA_WithoutExchangeRateDate_Rejects400_WithoutMutating()
    {
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");

        // Cruce de moneda con TC pero SIN fecha: 400 (el sistema no inventa la fecha del TC).
        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 60m, "USD", "Sin fecha del TC", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: true,
                exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.BNA_VendedorDivisa,
                exchangeRateDate: null));

        Assert.Equal("USD", h.Ctx.BookingCancellations.Single().PenaltyCurrencyAtEvent);
    }

    [Fact]
    public async Task Correct_CaseA_ManualExchangeRateWithoutJustification_Rejects400()
    {
        // TC cargado a mano (Manual) exige justificacion (INV-120). Sin ella, 400.
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 60m, "USD", "TC manual sin justificar", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: true,
                exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.Manual,
                exchangeRateDate: OperatorChargedDate, exchangeRateJustification: null));

        Assert.Equal("USD", h.Ctx.BookingCancellations.Single().PenaltyCurrencyAtEvent);
    }

    [Fact]
    public async Task Correct_SameCurrency_ArsOnArsInvoice_IsByteIdentical_NoConversionColumns()
    {
        // Mismo-moneda (ARS sobre factura ARS): comportamiento de hoy. No se pide TC y las columnas de conversion
        // quedan en null.
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 30_000m, declaredCurrency: "ARS");
        SetupCreateCapturesUser(h, _ => { });

        await h.Service.CorrectPenaltyAsync(
            bcId, 45_000m, "ARS", "Corregir el monto en pesos", "corrector", "Corrector", default,
            userCanClassifyAgencyPenalty: true);

        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(45_000m, after.PenaltyAmountAtEvent);
        Assert.Equal("ARS", after.PenaltyCurrencyAtEvent);
        Assert.Equal(DebitNoteStatus.Pending, after.DebitNoteStatus);
        // Sin conversion: las 6 columnas M2 quedan en null.
        Assert.Null(after.DeclaredPenaltyOriginalAmount);
        Assert.Null(after.DeclaredPenaltyOriginalCurrency);
        Assert.Null(after.PenaltyConversionExchangeRate);
        Assert.Null(after.PenaltyConversionExchangeRateSource);
        Assert.Null(after.PenaltyConversionExchangeRateAt);
        Assert.Null(after.PenaltyConversionExchangeRateJustification);
    }

    [Fact]
    public async Task Correct_CaseA_DoesNotCreateCancellationPenaltyDeduction()
    {
        // Anti-doble-cobro INV-ADR013-001: la conversion no crea una deduccion CancellationPenalty (la multa se
        // cobra por la ND, no por una deduccion del refund del cliente).
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");
        SetupCreateCapturesUser(h, _ => { });

        await h.Service.CorrectPenaltyAsync(
            bcId, 60m, "USD", "Convertir a pesos", "corrector", "Corrector", default,
            userCanClassifyAgencyPenalty: true,
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.BNA_VendedorDivisa,
            exchangeRateDate: OperatorChargedDate);

        Assert.Empty(h.Ctx.DeductionLines.Where(d => d.Kind == DeductionKind.CancellationPenalty));
    }

    [Fact]
    public async Task Situation_ExposesInvoiceCurrency_AndSuggestedExchangeRateDate()
    {
        // El read-model del paso expone la moneda de la factura (para saber cuando pedir TC) y la fecha sugerida.
        var h = BuildService();
        var (_, bc, supplier, reserva) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");
        bc.OperatorPenaltyConfirmedDate = OperatorChargedDate;
        await h.Ctx.SaveChangesAsync();

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal("ARS", sit.InvoiceCurrency); // factura en pesos (MonId=PES)
        Assert.Equal(OperatorChargedDate, sit.SuggestedExchangeRateDate);
    }

    [Fact]
    public async Task Correct_CaseA_WithAbsurdlyHighExchangeRate_Rejects400_WithoutMutating()
    {
        // Endurecimiento seguridad (2026-07-14): un TC absurdo (10^9) pasa IsUnreliableExchangeRate pero el techo
        // de cordura lo rechaza. 400, sin mutar.
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 60m, "USD", "TC absurdo", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: true,
                exchangeRate: 1_000_000_000m, exchangeRateSource: (int)ExchangeRateSource.Manual,
                exchangeRateDate: OperatorChargedDate, exchangeRateJustification: "test"));
        Assert.Contains("no parece un valor real", ex.Message, StringComparison.OrdinalIgnoreCase);

        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal("USD", after.PenaltyCurrencyAtEvent); // intacto
        Assert.Equal(60m, after.PenaltyAmountAtEvent);
        Assert.Null(after.DeclaredPenaltyOriginalAmount);
    }

    [Fact]
    public async Task Correct_CaseA_WithFractionalExchangeRate_Rejects400_WhereIsUnreliableWouldMiss()
    {
        // Endurecimiento seguridad (2026-07-14): un TC fraccionario (0,5) NO lo ataja IsUnreliableExchangeRate
        // (solo rechaza <=0 o ==1), pero el PISO de cordura si. Prueba que el piso cubre ese hueco.
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 60m, "USD", "TC fraccionario", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: true,
                exchangeRate: 0.5m, exchangeRateSource: (int)ExchangeRateSource.Manual,
                exchangeRateDate: OperatorChargedDate, exchangeRateJustification: "test"));

        Assert.Equal("USD", h.Ctx.BookingCancellations.Single().PenaltyCurrencyAtEvent);
    }

    [Fact]
    public async Task Correct_CaseA_WithFutureExchangeRateDate_Rejects400_WithoutMutating()
    {
        // Endurecimiento seguridad (2026-07-14): la fecha del TC no puede ser futura (defensa en profundidad, el
        // front ya la bloquea). 400, sin mutar.
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 60m, declaredCurrency: "USD");

        var futureDate = DateTime.UtcNow.Date.AddDays(30);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.CorrectPenaltyAsync(
                bcId, 60m, "USD", "Fecha futura", "corrector", "Corrector", default,
                userCanClassifyAgencyPenalty: true,
                exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.BNA_VendedorDivisa,
                exchangeRateDate: futureDate));
        Assert.Contains("no puede ser futura", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("USD", h.Ctx.BookingCancellations.Single().PenaltyCurrencyAtEvent);
    }

    [Fact]
    public async Task Correct_CaseA_ConvertedAmountExceedsInvoiceTotal_RoutesEmissionToManualReview_NoCae()
    {
        // Endurecimiento seguridad (2026-07-14): si el monto CONVERTIDO supera el total de la factura, la emision
        // de la ND rutea a revision manual (guard M2 del motor de emision) -> nunca saca CAE. El convertido igual
        // se persiste (M2); lo que queda pendiente es SOLO la emision.
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 200m, declaredCurrency: "USD");
        // Factura chica + cap grande: el convertido (200 * 1000 = 200.000) supera el total de la factura (50.000).
        var original = h.Ctx.Invoices.Single(i => i.TipoComprobante == 11);
        original.ImporteTotal = 50_000m;
        var line = h.Ctx.BookingCancellationLines.Single();
        line.RefundCap = 500_000m;
        await h.Ctx.SaveChangesAsync();
        var createCalls = 0;
        SetupCreateCapturesUser(h, _ => createCalls++);

        await h.Service.CorrectPenaltyAsync(
            bcId, 200m, "USD", "Convertido supera la factura", "corrector", "Corrector", default,
            userCanClassifyAgencyPenalty: true,
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.BNA_VendedorDivisa,
            exchangeRateDate: OperatorChargedDate);

        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(0, createCalls);                                  // no se emitio: sin CAE
        Assert.Null(after.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.ManualReview, after.DebitNoteStatus);
        // El convertido se persistio igual (queda para revision manual, no se pierde el dato).
        Assert.Equal(200_000m, after.PenaltyAmountAtEvent);
        Assert.Equal("ARS", after.PenaltyCurrencyAtEvent);
        Assert.Equal(200m, after.DeclaredPenaltyOriginalAmount);
    }

    [Fact]
    public async Task Correct_CaseA_NonRoundConversion_RoundsAwayFromZeroToTwoDecimals()
    {
        // Endurecimiento backend N1 (2026-07-14): conversion no-redonda. 150,5 USD * 1234,57 = 185.802,785, que a
        // 2 decimales con MidpointRounding.AwayFromZero da 185.802,79 (el redondeo bancario daria .78). Fija la
        // convencion de redondeo del sistema.
        var h = BuildService();
        var (bcId, bc, supplier, _) = await SeedPostNcAsync(h.Ctx);
        await SeedConfirmedManualReviewAsync(h, bc, supplier, penalty: 150.5m, declaredCurrency: "USD");
        // Factura y cap holgados para que emita limpio y el cap no recorte el monto convertido.
        var original = h.Ctx.Invoices.Single(i => i.TipoComprobante == 11);
        original.ImporteTotal = 1_000_000m;
        var line = h.Ctx.BookingCancellationLines.Single();
        line.RefundCap = 1_000_000m;
        await h.Ctx.SaveChangesAsync();
        SetupCreateCapturesUser(h, _ => { });

        await h.Service.CorrectPenaltyAsync(
            bcId, 150.5m, "USD", "Conversion no redonda", "corrector", "Corrector", default,
            userCanClassifyAgencyPenalty: true,
            exchangeRate: 1234.57m, exchangeRateSource: (int)ExchangeRateSource.BNA_VendedorDivisa,
            exchangeRateDate: OperatorChargedDate);

        var after = h.Ctx.BookingCancellations.Single();
        Assert.Equal(185_802.79m, after.PenaltyAmountAtEvent);      // AwayFromZero (no 185.802,78)
        Assert.Equal(1234.57m, after.PenaltyConversionExchangeRate);
        Assert.Equal(150.5m, after.DeclaredPenaltyOriginalAmount);
    }

    /// <summary>
    /// Semilla Caso B: multa confirmada declarada en USD con la linea del operador TAMBIEN en USD (el operador
    /// internacional retuvo dolares), pero la factura del cliente esta en pesos. La correccion cross-currency no
    /// se puede resolver con un solo TC.
    /// </summary>
    private static async Task SeedConfirmedCrossCurrencyOperatorLineAsync(
        Harness h, BookingCancellation bc, Supplier supplier, decimal declaredUsd)
    {
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.PenaltyAmountAtEvent = declaredUsd;
        bc.PenaltyCurrencyAtEvent = "USD";
        bc.DebitNotePurpose = DebitNotePurpose.PenaltyOrCancellationCharge;
        bc.DebitNoteStatus = DebitNoteStatus.ManualReview;
        bc.DebitNoteInvoiceId = null;
        bc.PenaltyConfirmedByUserId = "u";
        bc.PenaltyConfirmedByUserName = "U";
        bc.PenaltyConfirmedAt = DateTime.UtcNow.AddDays(-1);
        bc.ConceptClassifiedByUserId = "u";
        bc.ConceptClassifiedByUserName = "U";
        bc.ConceptClassifiedAt = DateTime.UtcNow.AddDays(-1);

        h.Ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = supplier.Id,
            Currency = Monedas.USD, // <- la linea del operador esta en dolares (el eje que hace Caso B)
            RefundCap = 500m,
            PenaltyAmount = declaredUsd,
            RetainedDeductionAmount = declaredUsd,
            PenaltyStatus = PenaltyStatus.Confirmed,
            ReceivedRefundAmount = 0m,
            RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund,
        });
        await h.Ctx.SaveChangesAsync();
    }
}
