using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-013 / ADR-014 (§6, 2026-06-02) — tests de INTEGRACION (Postgres real via
/// <see cref="PostgresIntegrationFixture"/>) del flujo de cancelacion con Nota de Debito (ND).
///
/// <para><b>Por que integracion y no unit</b>: los unit tests ya existentes
/// (<c>CancellationDeferredPenaltyTests</c>, <c>CancellationDebitNoteCaptureTests</c>,
/// <c>CancellationDebitNoteGatingTests</c>) cubren las precondiciones del endpoint, la captura,
/// las guardas y el gating con EF InMemory + mocks. Lo que InMemory NO puede validar y SOLO
/// integracion ejercita:
/// <list type="bullet">
///   <item><b>El commit propio de la marca de no-retorno (B1, §3.4)</b>: que
///   <c>PenaltyStatus=Confirmed</c> se persiste DURABLE en su propia transaccion ANTES de crear
///   la ND, y que un reintento posterior rebota — contra una BD real, no contra un
///   ChangeTracker en memoria.</item>
///   <item><b>El concurrency token xmin</b> (caso 6): dos <c>confirm-penalty</c> concurrentes
///   sobre el mismo BC -> uno gana, el otro <see cref="DbUpdateConcurrencyException"/>. InMemory
///   no implementa xmin, asi que un unit test pasaria sin proteger nada.</item>
///   <item><b>La persistencia transaccional de la ND</b>: que la Invoice ND insertada por el
///   pipeline (mock que escribe la fila real) se resuelve por PublicId contra la BD y queda
///   vinculada (<c>DebitNoteInvoiceId</c>), con su CHECK constraint
///   <c>chk_BookingCancellations_fiscalsnapshot_consistent</c> respetado.</item>
///   <item><b>El anti-doble-cobro RE-evaluado en runtime (R13, §3.8)</b> con una query fresca
///   sobre <c>OperatorRefundAllocations</c> reales en la BD (caso 8).</item>
/// </list>
/// </para>
///
/// <para><b>Que NO se cubre aca y por que</b>: la emision real al ARCA (CAE) NO ocurre — el
/// pipeline (<c>IInvoiceService.CreateAsync</c>) esta mockeado para insertar la fila Invoice ND
/// pero NO llamar a AFIP (igual que el resto de la suite de integracion del modulo). El paso
/// async ND -> CAE lo emite <c>ProcessInvoiceJob</c> en produccion y se reconcilia por la
/// bandeja; aca simulamos el resultado seteando <c>Invoice.Resultado</c> a mano cuando hace
/// falta. El permiso server-side (resuelto en el controller contra los claims) y los codigos
/// HTTP exactos NO se ejercitan aca: este es un test de SERVICE (mismo nivel que
/// <c>BookingCancellationServiceF2_3IntegrationTests</c>), no un test de endpoint HTTP via
/// <c>WebApplicationFactory</c> — el proyecto NO tiene esa fixture, ver el reporte de QA.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CancellationDebitNoteDeferredIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public CancellationDebitNoteDeferredIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    // Reset antes de cada test para que sean independientes (mismo patron que el resto del modulo).
    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Bundle del service: calculator REAL + settings con el flag de la ND configurable.
    // El IInvoiceService es un mock que, en CreateAsync, INSERTA una fila Invoice ND real
    // en Postgres y devuelve su DTO — asi TryEmitCancellationDebitNoteAsync puede resolver
    // su Id por PublicId contra la BD y vincularla (lo que un mock no-op no permitiria probar).
    // =========================================================================

    private record ServiceBundle(
        BookingCancellationService Service,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceMock);

    private ServiceBundle BuildService(
        bool debitNoteFlagOn = true,
        AppDbContext? ctxIn = null)
    {
        var ctx = ctxIn ?? _fixture.CreateDbContext();

        var invoiceMock = new Mock<IInvoiceService>();
        // EnqueueAnnulmentAsync (NC total) no-op: en estos tests la NC ya esta emitida en el seed.
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                // EnablePartialCreditNotes OFF a proposito: estos tests son del flujo de ND, no
                // del de NC parcial. Lo dejamos apagado para que ConfirmAsync (cuando se use)
                // tome el path FC1.2 (NC total) y no se desvie a la maquina de manual review.
                EnablePartialCreditNotes = false,
                EnableCancellationDebitNote = debitNoteFlagOn,
                OnePerReservaInvoicePolicy = true,
                OperatorRefundTimeoutDays = 60,
                CancellationDebitNoteGraceDays = 15,
                CancellationDebitNoteHardWarnDays = 60,
                CancellationDebitNoteFourEyesThreshold = 2_000_000m,
            });

        var approvalService = new ApprovalRequestService(ctx, settingsMock.Object);
        var auditRepo = new Repository<AuditLog>(ctx);
        var auditService = new AuditService(auditRepo, NullLogger<AuditService>.Instance);
        var calculator = new FiscalLiquidationCalculator(NullLogger<FiscalLiquidationCalculator>.Instance);

        var adminCountMock = new Mock<IAdminUserCountService>();
        adminCountMock
            .Setup(a => a.CountActiveAdminsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, approvalService, auditService,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object, calculator, adminCountMock.Object);

        return new ServiceBundle(service, ctx, invoiceMock);
    }

    /// <summary>
    /// Configura el mock de <c>CreateAsync</c> para que, al emitir la ND, INSERTE una Invoice
    /// real (ND C = 12) en Postgres asociada a la factura original, con CAE simulado y
    /// <c>Resultado</c> configurable. Devuelve su DTO (PublicId) para que el service la resuelva
    /// y la vincule. Es el analogo de integracion del <c>SetupCreateEmitsDebitNote</c> del unit:
    /// la fila se persiste con su PROPIO context efimero (simula que el pipeline async la emite
    /// en su propio scope/transaccion, no en el ChangeTracker del service).
    /// </summary>
    private void SetupCreateEmitsRealDebitNote(
        ServiceBundle bundle, int originalInvoiceId, int reservaId, string resultado = "PENDING")
    {
        bundle.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                // Context de escritura independiente: simula que la ND la persiste el pipeline
                // async en su propio scope. Hace COMMIT real (SaveChanges) para que el service
                // luego la lea por PublicId contra la BD.
                using var writeCtx = _fixture.CreateDbContext();
                var nd = new Invoice
                {
                    TipoComprobante = 12, // ND C (derivada de factura C = 11)
                    PuntoDeVenta = 1,
                    NumeroComprobante = 5000 + originalInvoiceId,
                    // Resultado configurable: "PENDING" = en vuelo, "A"+CAE = aprobada,
                    // "R" = rechazada. Permite a los tests de la bandeja reconciliar.
                    Resultado = resultado,
                    CAE = resultado == "A" ? "99000000000001" : null,
                    VencimientoCAE = resultado == "A" ? DateTime.UtcNow.AddDays(10) : null,
                    MonId = "PES",
                    ImporteTotal = req.Items.Sum(i => i.Total),
                    ImporteNeto = req.Items.Sum(i => i.Total),
                    ImporteIva = 0m,
                    ReservaId = reservaId,
                    OriginalInvoiceId = originalInvoiceId,
                    AnnulmentStatus = AnnulmentStatus.None,
                    CreatedAt = DateTime.UtcNow,
                };
                writeCtx.Invoices.Add(nd);
                writeCtx.SaveChanges();
                return new InvoiceDto { PublicId = nd.PublicId };
            });
    }

    /// <summary>
    /// Configura <c>CreateAsync</c> para que devuelva un DTO con un PublicId que NO existe en la
    /// base (NO inserta ninguna fila Invoice). Simula la rama defensiva de
    /// <c>TryEmitCancellationDebitNoteAsync</c>: el pipeline dijo "cree la ND" pero el service no
    /// puede resolver su Id legacy por PublicId contra la BD -> la ND queda sin vincular y el BC
    /// se rutea a ManualReview (no se considera emitida). Es la rama complementaria al happy-path
    /// de <see cref="SetupCreateEmitsRealDebitNote"/>.
    /// </summary>
    private static void SetupCreateReturnsUnresolvableDebitNote(ServiceBundle bundle)
    {
        bundle.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            // PublicId aleatorio que ninguna Invoice de la base tiene -> FirstOrDefaultAsync por
            // PublicId devuelve null -> debitNoteId is null -> RouteDebitNoteToManualReviewAsync.
            .ReturnsAsync(new InvoiceDto { PublicId = Guid.NewGuid() });
    }

    /// <summary>
    /// Seed del caso post-NC (el escenario tipico del Dia N): factura C=11 con CAE, NC C=13
    /// con CAE, BC en <see cref="BookingCancellationStatus.AwaitingOperatorRefund"/> con
    /// <c>CreditNoteInvoiceId</c> seteado. El concepto/estado de la penalidad se controla por
    /// parametro para cada escenario (default conservador: pass-through / Estimated).
    /// </summary>
    private async Task<(Guid BcPublicId, int BcId, int OriginalInvoiceId, int ReservaId, int SupplierId)>
        SeedPostNcBcAsync(
            AppDbContext ctx,
            PenaltyOwnership supplierOwnership = PenaltyOwnership.Agency,
            CancellationConceptKind conceptKind = CancellationConceptKind.OperatorPenaltyPassThrough,
            PenaltyStatus penaltyStatus = PenaltyStatus.Estimated,
            int originalTipoComprobante = 11,
            string originalMonId = "PES",
            decimal originalTotal = 100_000m,
            DateTime? confirmedWithClientAt = null,
            // Para el path SINCRONO (Dia 0): el BC todavia NO transiciono via la NC. Permite
            // sembrarlo en AwaitingFiscalConfirmation (sin CreditNoteInvoiceId) y con la
            // clasificacion/confirmacion de la penalidad YA aplicada (como la deja la captura
            // sincrona de ConfirmAsync), para luego disparar OnArcaSucceededAsync.
            BookingCancellationStatus status = BookingCancellationStatus.AwaitingOperatorRefund,
            bool linkCreditNote = true,
            decimal? penaltyAmountAtEvent = null,
            DebitNotePurpose? debitNotePurpose = null,
            bool seedClassificationAudit = false)
    {
        var customer = new Customer
        {
            FullName = "Cliente ADR-014",
            TaxCondition = "Consumidor Final",
            IsActive = true,
            TaxId = "20111111111",
        };
        var supplier = new Supplier
        {
            Name = "Operador ADR-014",
            IsActive = true,
            TaxCondition = "IVA_RESP_INSCRIPTO",
            PenaltyOwnership = supplierOwnership,
        };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"ADR014-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Reserva ADR-014",
            Status = EstadoReserva.PendingOperatorRefund,
            PayerId = customer.Id,
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = originalTipoComprobante, // 11 = Factura C
            PuntoDeVenta = 1,
            NumeroComprobante = 100,
            CAE = "12345678901234",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            Resultado = "A",
            MonId = originalMonId,
            ImporteTotal = originalTotal,
            ImporteNeto = originalTotal,
            ImporteIva = 0m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        // NC total C = 13 (la que ya salio con CAE). Asociada a la original.
        var creditNote = new Invoice
        {
            TipoComprobante = 13, // NC C
            PuntoDeVenta = 1,
            NumeroComprobante = 101,
            CAE = "99999999999999",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            Resultado = "A",
            MonId = "PES",
            ImporteTotal = originalTotal,
            ImporteNeto = originalTotal,
            ImporteIva = 0m,
            ReservaId = reserva.Id,
            OriginalInvoiceId = original.Id,
            AnnulmentStatus = AnnulmentStatus.None,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        // BC post-NC. El CHECK chk_BookingCancellations_fiscalsnapshot_consistent exige
        // FiscalSnapshot coherente para Status fuera de Drafted/Aborted, asi que el snapshot
        // va COMPLETO (Source != Unset, TC > 0, Currency != null).
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id,
            // En el path sincrono (Dia 0) la NC aun no obtuvo CAE -> el BC todavia no tiene
            // CreditNoteInvoiceId; lo setea OnArcaSucceededAsync. Por eso es configurable.
            CreditNoteInvoiceId = linkCreditNote ? creditNote.Id : (int?)null,
            Status = status,
            Reason = "Cliente cancelo; penalidad propia a confirmar por el operador",
            DraftedAt = DateTime.UtcNow.AddDays(-12),
            DraftedByUserId = "vendedor-1",
            DraftedByUserName = "Vendedor 1",
            ConfirmedByUserId = "vendedor-1",
            ConfirmedByUserName = "Vendedor 1",
            ConfirmedWithClientAt = confirmedWithClientAt ?? DateTime.UtcNow.AddDays(-10),
            AmountPaidAtCancellation = originalTotal,
            EstimatedRefundAmount = originalTotal,
            ConceptKind = conceptKind,
            PenaltyStatus = penaltyStatus,
            PenaltyAmountAtEvent = penaltyAmountAtEvent,
            DebitNotePurpose = debitNotePurpose,
            DebitNoteStatus = DebitNoteStatus.NotApplicable,
            // Para el Dia 0: la captura sincrona ya dejo el rastro de QUIEN clasifico el
            // concepto y QUIEN confirmo la penalidad (el gating de la ND lo exige como
            // invariante, B3). Lo sembramos solo cuando el test simula ese estado ya capturado.
            ConceptClassifiedByUserId = seedClassificationAudit ? "backoffice" : null,
            ConceptClassifiedByUserName = seedClassificationAudit ? "Back Office" : null,
            ConceptClassifiedAt = seedClassificationAudit ? DateTime.UtcNow.AddMinutes(-5) : null,
            PenaltyConfirmedByUserId = seedClassificationAudit ? "backoffice" : null,
            PenaltyConfirmedByUserName = seedClassificationAudit ? "Back Office" : null,
            PenaltyConfirmedAt = seedClassificationAudit ? DateTime.UtcNow.AddMinutes(-5) : null,
            OperatorPenaltyConfirmedDate = seedClassificationAudit ? DateTime.UtcNow.AddDays(-2) : null,
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS",
                ManualJustification = "Seed ADR-014",
                FetchedAt = DateTime.UtcNow,
                AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent = "IVA_RESP_INSCRIPTO",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc.PublicId, bc.Id, original.Id, reserva.Id, supplier.Id);
    }

    private static ConfirmPenaltyRequest PenaltyRequest(
        decimal amount = 30_000m,
        DateTime? operatorDate = null,
        string? support = "https://docs/acuerdo-operador.pdf",
        CancellationConceptKind? concept = CancellationConceptKind.AgencyManagementFee)
        => new ConfirmPenaltyRequest(
            ConceptKind: concept,
            ConfirmedPenaltyAmount: amount,
            OperatorConfirmationDate: operatorDate ?? DateTime.UtcNow.AddDays(-2),
            DebitNotePurpose: null,
            SupportingDocumentReference: support);

    // =========================================================================
    // Caso 4 (ADR-014 §6) — confirm-penalty diferido emite la ND y la VINCULA en Postgres.
    // Es el camino feliz del flujo diferido: el unit cubre la transicion de estado en memoria;
    // aca validamos que la Invoice ND se persiste, se resuelve por PublicId contra la BD real
    // y queda vinculada (DebitNoteInvoiceId), con el CbteTipo correcto congelado.
    // =========================================================================

    /// <summary>
    /// Verifica que confirmar la penalidad propia (concepto agency-owned) sobre un BC con la NC
    /// ya emitida persiste <c>PenaltyStatus=Confirmed</c> y emite + VINCULA una ND C real en la
    /// base. Cubre el caso 4 del ADR-014 §6 a nivel de persistencia transaccional.
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_AgencyOwnedConfirmed_PersistsConfirmedMark_EmitsAndLinksRealDebitNote()
    {
        // ARRANGE — BC post-NC con concepto propio aun ESTIMADO (el operador todavia no confirmo).
        var bundle = BuildService(debitNoteFlagOn: true);
        var (bcPublicId, bcId, originalInvoiceId, reservaId, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Estimated);
        SetupCreateEmitsRealDebitNote(bundle, originalInvoiceId, reservaId);

        // ACT — el operador confirmo $30.000 con soporte documental (no dispara 4-eyes).
        await bundle.Service.ConfirmPenaltyAsync(
            bcPublicId, PenaltyRequest(amount: 30_000m),
            "backoffice", "Back Office", requesterIsAdmin: false, CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        // ASSERT — leemos el estado DURABLE desde un context fresco (no el ChangeTracker).
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);

        // La penalidad quedo Confirmed (marca de no-retorno persistida) con su monto y fecha.
        Assert.Equal(PenaltyStatus.Confirmed, bcAfter.PenaltyStatus);
        Assert.Equal(30_000m, bcAfter.PenaltyAmountAtEvent);
        Assert.NotNull(bcAfter.OperatorPenaltyConfirmedDate);

        // La ND quedo encolada (Pending) y VINCULADA al BC contra la BD real.
        Assert.Equal(DebitNoteStatus.Pending, bcAfter.DebitNoteStatus);
        Assert.NotNull(bcAfter.DebitNoteInvoiceId);

        // El CbteTipo congelado de la ND es 12 (ND C, derivado de la factura original C=11).
        Assert.Equal(12, bcAfter.DebitNoteCbteTipoAtEvent);

        // Existe EXACTAMENTE una Invoice ND (12) en la base para esa factura original.
        var ndCount = await verifyCtx.Invoices.AsNoTracking()
            .CountAsync(i => i.TipoComprobante == 12 && i.OriginalInvoiceId == originalInvoiceId);
        Assert.Equal(1, ndCount);
    }

    // =========================================================================
    // Caso 4-bis — rama complementaria del happy-path: el pipeline "crea" la ND pero el service
    // NO puede resolver su Id por PublicId (DTO con PublicId inexistente). La ND no se vincula
    // y el BC va a ManualReview. Cubre la rama `debitNoteId is null` de TryEmit (§ linea 2048).
    // =========================================================================

    /// <summary>
    /// Si <c>CreateAsync</c> devuelve un DTO cuyo PublicId no se puede resolver contra la BD (la
    /// ND no quedo persistida/resoluble), <c>confirm-penalty</c> deja la penalidad Confirmed pero
    /// rutea la ND a <c>ManualReview</c>: queda SIN <c>DebitNoteInvoiceId</c> y NO se considera
    /// emitida. Es la rama defensiva complementaria al happy-path (caso 4).
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_PipelineReturnsUnresolvableDebitNote_RoutesToManualReview_NotLinked()
    {
        // ARRANGE — BC post-NC con concepto propio estimado; el pipeline devolvera un PublicId
        // que no existe en la base (no inserta la fila Invoice ND).
        var bundle = BuildService(debitNoteFlagOn: true);
        var (bcPublicId, bcId, originalInvoiceId, _, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Estimated);
        SetupCreateReturnsUnresolvableDebitNote(bundle);

        // ACT — confirmamos la penalidad. El gating pasa, pero la vinculacion falla.
        await bundle.Service.ConfirmPenaltyAsync(
            bcPublicId, PenaltyRequest(),
            "backoffice", "Back Office", false, CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        // ASSERT — la marca Confirmed se persiste (exactly-once), pero la ND NO quedo vinculada
        // ni emitida: el BC esta en ManualReview para que la bandeja lo levante.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.Equal(PenaltyStatus.Confirmed, bcAfter.PenaltyStatus);
        Assert.Equal(DebitNoteStatus.ManualReview, bcAfter.DebitNoteStatus);
        Assert.Null(bcAfter.DebitNoteInvoiceId);

        // No hay NINGUNA ND (12) en la base para esa factura: el pipeline nunca persistio la fila.
        var ndCount = await verifyCtx.Invoices.AsNoTracking()
            .CountAsync(i => i.TipoComprobante == 12 && i.OriginalInvoiceId == originalInvoiceId);
        Assert.Equal(0, ndCount);
    }

    // =========================================================================
    // Caso 1 — pass-through: confirm-penalty rebota, NO se emite ND, no muta nada fiscal.
    // (El reverso: cuando el operador retiene la penalidad, el endpoint diferido no aplica.)
    // =========================================================================

    /// <summary>
    /// Cuando el concepto es pass-through (la penalidad la retiene el operador), confirm-penalty
    /// rebota con INV-ADR014-002 y NO se persiste ninguna ND ni se confirma la penalidad.
    /// Cubre el caso 1 del ADR-014 §6 (solo NC, sin ND) desde el flujo diferido.
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_PassThroughConcept_Rejects_NoDebitNoteEmitted()
    {
        // ARRANGE — operador retiene la penalidad; pedimos confirm SIN concepto explicito
        // (el default por operador es pass-through).
        var bundle = BuildService(debitNoteFlagOn: true);
        var (bcPublicId, bcId, originalInvoiceId, reservaId, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Operator,
            conceptKind: CancellationConceptKind.OperatorPenaltyPassThrough);
        // El mock esta configurado, pero la emision NO debe llegar a llamarse (rebota antes).
        SetupCreateEmitsRealDebitNote(bundle, originalInvoiceId, reservaId);

        // ACT + ASSERT — rebota por concepto no agency-owned.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            bundle.Service.ConfirmPenaltyAsync(
                bcPublicId, PenaltyRequest(concept: null),
                "backoffice", "Back Office", false, CancellationToken.None,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-002", ex.InvariantCode);

        // NADA muto en la BD: ni ND emitida ni penalidad confirmada.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.NotEqual(PenaltyStatus.Confirmed, bcAfter.PenaltyStatus);
        Assert.Null(bcAfter.DebitNoteInvoiceId);
        var ndCount = await verifyCtx.Invoices.AsNoTracking().CountAsync(i => i.TipoComprobante == 12);
        Assert.Equal(0, ndCount);
    }

    // =========================================================================
    // Caso 3 — penalidad ESTIMADA de cargo propio: NO se confirma todavia (no se llama
    // confirm-penalty) y el BC aparece en la bandeja con el pseudo-estado de "pendiente de
    // confirmacion". Es el caso dominante del negocio. Aca lo validamos contra Postgres real
    // (el unit ya lo cubre con InMemory; el valor extra es el query real con joins a Reserva).
    // =========================================================================

    /// <summary>
    /// Un BC con concepto propio ESTIMADO, NC total ya emitida y sin ND debe aparecer en la
    /// bandeja <c>GetCancellationsWithMissingDebitNote</c> con el pseudo-estado
    /// <c>EstimatedPendingConfirmation</c>, para que el frontend abra el modal de confirmacion.
    /// </summary>
    [Fact]
    public async Task EstimatedAgencyOwnedWithCreditNote_AppearsInPendingDebitNotesBandeja()
    {
        // ARRANGE — concepto propio, penalidad ESTIMADA (sin confirmar), NC emitida, sin ND.
        var bundle = BuildService(debitNoteFlagOn: true);
        var (bcPublicId, _, _, _, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyCancellationFee,
            penaltyStatus: PenaltyStatus.Estimated);

        // ACT — consultamos la bandeja contra la BD real.
        var rows = await bundle.Service.GetCancellationsWithMissingDebitNoteAsync(CancellationToken.None);

        // ASSERT — el BC aparece con el pseudo-estado de "pendiente de confirmacion".
        Assert.Contains(rows, r =>
            r.BookingCancellationPublicId == bcPublicId &&
            r.DebitNoteStatus == CancellationDebitNotePendingDto.EstimatedPendingConfirmationPseudoStatus);
    }

    // =========================================================================
    // Caso 5 — exactly-once (B1): un segundo confirm-penalty tras el exito rebota con
    // INV-ADR014-003 y NO crea una segunda ND. La diferencia con el unit: aca la marca
    // Confirmed se lee DURABLE de Postgres tras su commit propio (no del ChangeTracker), y
    // contamos las Invoice ND reales en la base.
    // =========================================================================

    /// <summary>
    /// Tras un confirm-penalty exitoso, un reintento sobre el MISMO BC rebota con INV-ADR014-003
    /// y existe UNA sola ND en la base. Valida el exactly-once (B1) contra persistencia real.
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_RetryAfterSuccess_RejectsInv003_OnlyOneDebitNotePersisted()
    {
        // ARRANGE
        var bundle = BuildService(debitNoteFlagOn: true);
        var (bcPublicId, _, originalInvoiceId, reservaId, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Estimated);
        SetupCreateEmitsRealDebitNote(bundle, originalInvoiceId, reservaId);

        // ACT — primera confirmacion: exitosa.
        await bundle.Service.ConfirmPenaltyAsync(
            bcPublicId, PenaltyRequest(),
            "backoffice", "Back Office", false, CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        // Segundo intento: usamos un service/context FRESCO para forzar la lectura desde la BD
        // (no desde el ChangeTracker del primero) — asi probamos que la marca Confirmed quedo
        // DURABLE y el pre-check de idempotencia la ve.
        var bundle2 = BuildService(debitNoteFlagOn: true);
        SetupCreateEmitsRealDebitNote(bundle2, originalInvoiceId, reservaId);

        // ASSERT — el reintento rebota por idempotencia (INV-ADR014-003).
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            bundle2.Service.ConfirmPenaltyAsync(
                bcPublicId, PenaltyRequest(),
                "backoffice", "Back Office", false, CancellationToken.None,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-003", ex.InvariantCode);

        // En la base hay UNA sola ND para esa factura original.
        await using var verifyCtx = _fixture.CreateDbContext();
        var ndCount = await verifyCtx.Invoices.AsNoTracking()
            .CountAsync(i => i.TipoComprobante == 12 && i.OriginalInvoiceId == originalInvoiceId);
        Assert.Equal(1, ndCount);
    }

    // =========================================================================
    // Caso 6 — concurrencia xmin: dos confirm-penalty concurrentes sobre el MISMO BC.
    // ESTE es el test que SOLO Postgres real puede validar (InMemory ignora xmin).
    // =========================================================================

    /// <summary>
    /// Dos confirmaciones de penalidad concurrentes sobre el mismo BC: A gana y B choca
    /// ESTRICTAMENTE con <see cref="DbUpdateConcurrencyException"/> por el concurrency token
    /// xmin (que el controller traduce a 409 CONCURRENT_EDIT). Gracias a que la marca Confirmed
    /// se persiste en el commit propio del paso c ANTES de crear la ND, el rechazo es seguro y
    /// NO quedan dos ND.
    ///
    /// <para><b>Por que este test es ESTRICTO y no disyuntivo</b>: un assert que acepte
    /// "DbUpdateConcurrencyException OR INV-ADR014-003 (idempotencia)" puede pasar SIEMPRE por
    /// la rama idempotencia y no ejercitar nunca el token xmin — daria falsa confianza: seguiria
    /// verde aunque alguien quitara <c>UseXminAsConcurrencyToken</c>. La idempotencia secuencial
    /// (reintento tras exito) ya la cubre <c>ConfirmPenalty_RetryAfterSuccess_RejectsInv003...</c>;
    /// aca exigimos SOLO la rama xmin.</para>
    ///
    /// <para><b>Patron determinista</b> (sin Thread.Sleep), mismo que <c>XminConcurrencyTests</c>:
    /// pre-cargamos el MISMO BC en los DOS contexts CON TRACKING (sin AsNoTracking) ANTES de que A
    /// persista. Asi cada context cachea la entidad con el xmin ACTUAL. Cuando
    /// <c>ConfirmPenaltyAsync</c> de B corre su query interna (<c>FirstOrDefaultAsync</c>
    /// trackeado), EF devuelve la instancia cacheada con el snapshot VIEJO (PenaltyStatus=Estimated,
    /// xmin viejo) en vez de releer la fila — por eso el pre-check de idempotencia (precondicion 6,
    /// que lee bc.PenaltyStatus) NO ve el Confirmed de A y B avanza hasta el SaveChanges del paso c,
    /// que detecta el xmin desactualizado y tira DbUpdateConcurrencyException. Sin este pre-load
    /// con tracking, B releeria el BC fresco tras el commit de A y rebotaria por idempotencia
    /// (la rama que NO queremos ejercitar aca).</para>
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_TwoConcurrentConfirmations_LoserThrowsDbUpdateConcurrencyException_OnlyOneDebitNote()
    {
        // ARRANGE — un unico BC post-NC con concepto propio estimado.
        var seedBundle = BuildService(debitNoteFlagOn: true);
        var (bcPublicId, bcId, originalInvoiceId, reservaId, _) = await SeedPostNcBcAsync(
            seedBundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Estimated);

        // Dos services con contexts INDEPENDIENTES (como dos sesiones HTTP en paralelo).
        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();
        var bundleA = BuildService(debitNoteFlagOn: true, ctxIn: ctxA);
        var bundleB = BuildService(debitNoteFlagOn: true, ctxIn: ctxB);
        SetupCreateEmitsRealDebitNote(bundleA, originalInvoiceId, reservaId);
        SetupCreateEmitsRealDebitNote(bundleB, originalInvoiceId, reservaId);

        // Pre-load CON TRACKING en AMBOS contexts, ANTES del commit de A (ver el <summary>: es
        // lo que fuerza que B trabaje sobre el snapshot viejo y la rama que rebota sea la xmin).
        await ctxA.BookingCancellations.FirstAsync(b => b.Id == bcId);
        await ctxB.BookingCancellations.FirstAsync(b => b.Id == bcId);

        // ACT — A confirma y persiste primero: el xmin de la fila avanza en la BD.
        await bundleA.Service.ConfirmPenaltyAsync(
            bcPublicId, PenaltyRequest(),
            "backoffice-A", "Back Office A", false, CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        // ASSERT — B confirma con su snapshot viejo. El SaveChanges del paso c DEBE chocar
        // ESTRICTAMENTE por xmin. Si esto NO se lanza, el token xmin no esta protegiendo el BC
        // bajo concurrencia (race silencioso: dos ND posibles).
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            bundleB.Service.ConfirmPenaltyAsync(
                bcPublicId, PenaltyRequest(),
                "backoffice-B", "Back Office B", false, CancellationToken.None,
                userCanClassifyAgencyPenalty: true));

        // En la base hay UNA sola ND: la concurrencia no produjo doble emision. (B choco en el
        // paso c, ANTES de crear su ND — por eso solo existe la de A.)
        await using var verifyCtx = _fixture.CreateDbContext();
        var ndCount = await verifyCtx.Invoices.AsNoTracking()
            .CountAsync(i => i.TipoComprobante == 12 && i.OriginalInvoiceId == originalInvoiceId);
        Assert.Equal(1, ndCount);
    }

    // =========================================================================
    // Caso 7 — flag OFF: confirm-penalty rechaza y NO muta nada (byte-identico a pre-ADR-014).
    // =========================================================================

    /// <summary>
    /// Con <c>EnableCancellationDebitNote=false</c>, confirm-penalty rechaza con
    /// InvalidOperationException y NO persiste ningun cambio fiscal de penalidad ni ND.
    /// Valida la byte-identidad del flag OFF contra la BD real.
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_FeatureFlagOff_Rejects_NoMutationPersisted()
    {
        // ARRANGE — flag OFF; concepto propio estimado en el seed.
        var bundle = BuildService(debitNoteFlagOn: false);
        var (bcPublicId, bcId, _, _, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Estimated);

        // ACT + ASSERT — rechaza (flag maestro OFF).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bundle.Service.ConfirmPenaltyAsync(
                bcPublicId, PenaltyRequest(),
                "backoffice", "Back Office", false, CancellationToken.None,
                userCanClassifyAgencyPenalty: true));

        // NADA muto en la BD.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.Equal(PenaltyStatus.Estimated, bcAfter.PenaltyStatus);
        Assert.Null(bcAfter.OperatorPenaltyConfirmedDate);
        Assert.Equal(DebitNoteStatus.NotApplicable, bcAfter.DebitNoteStatus);
        Assert.Null(bcAfter.DebitNoteInvoiceId);
    }

    // =========================================================================
    // Caso 8 — anti-doble-cobro Dia N (R13, §3.8): si entre el Dia 0 y el confirm-penalty se
    // cargo una deduction CancellationPenalty en el refund del operador, confirm-penalty
    // RE-evalua en runtime (query fresca contra Postgres) y rutea a ManualReview SIN emitir ND.
    // La marca Confirmed igual se persiste (exactly-once). InMemory tambien lo soporta, pero
    // aca la disyuncion corre contra OperatorRefundAllocations reales con su CHECK constraint.
    // =========================================================================

    /// <summary>
    /// Si existe una deduccion <c>CancellationPenalty</c> cargada en el refund del operador para
    /// este BC, confirm-penalty deja la penalidad Confirmed pero rutea la ND a ManualReview (no
    /// emite) — para no cobrar la penalidad dos veces. Cubre el caso 8 / R13 del ADR-014.
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_WithPenaltyDeductionLoadedAfterDay0_RoutesToManualReview_NoDebitNote()
    {
        // ARRANGE — BC post-NC con concepto propio estimado.
        var bundle = BuildService(debitNoteFlagOn: true);
        var (bcPublicId, bcId, originalInvoiceId, reservaId, supplierId) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Estimated);
        SetupCreateEmitsRealDebitNote(bundle, originalInvoiceId, reservaId);

        // Entre el Dia 0 y el Dia N alguien cargo una deduction de penalidad en el refund del
        // operador para este BC. Persistimos refund + allocation + deduction REALES (sus CHECK
        // de Postgres exigen NetAmount >= 0, GrossAmount >= NetAmount, DeductionLine.Amount > 0).
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var refund = new OperatorRefundReceived
            {
                SupplierId = supplierId,
                ReceivedAmount = 50_000m,
                AllocatedAmount = 0m,
                Currency = "ARS",
                ExchangeRateAtReceipt = 1m,
                Method = "Transfer",
                ReceivedAt = DateTime.UtcNow,
                ReceivedByUserId = "cashier",
                ReceivedByUserName = "Cashier",
            };
            seedCtx.OperatorRefundReceived.Add(refund);
            await seedCtx.SaveChangesAsync();

            var allocation = new OperatorRefundAllocation
            {
                OperatorRefundReceivedId = refund.Id,
                BookingCancellationId = bcId,
                GrossAmount = 50_000m,
                NetAmount = 20_000m,
                IsVoided = false,
                CreatedByUserId = "cashier",
                CreatedAt = DateTime.UtcNow,
            };
            allocation.Deductions.Add(new DeductionLine
            {
                Kind = DeductionKind.CancellationPenalty,
                Amount = 30_000m,
            });
            seedCtx.OperatorRefundAllocations.Add(allocation);
            await seedCtx.SaveChangesAsync();
        }

        // ACT — confirmamos la penalidad. El gating de la ND re-chequea en runtime.
        await bundle.Service.ConfirmPenaltyAsync(
            bcPublicId, PenaltyRequest(),
            "backoffice", "Back Office", false, CancellationToken.None,
            userCanClassifyAgencyPenalty: true);

        // ASSERT — la marca Confirmed se persiste (exactly-once) pero la ND NO se emite: ruteada
        // a ManualReview por la disyuncion anti-doble-cobro.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.Equal(PenaltyStatus.Confirmed, bcAfter.PenaltyStatus);
        Assert.Equal(DebitNoteStatus.ManualReview, bcAfter.DebitNoteStatus);
        Assert.Null(bcAfter.DebitNoteInvoiceId);

        var ndCount = await verifyCtx.Invoices.AsNoTracking().CountAsync(i => i.TipoComprobante == 12);
        Assert.Equal(0, ndCount);
    }

    // =========================================================================
    // Caso 9 — permiso / 4-eyes.
    // =========================================================================

    /// <summary>
    /// Sin el permiso <c>cancellations.classify_agency_penalty</c>, confirm-penalty se rechaza
    /// con INV-ADR014-PERM y no muta nada. (El controller resuelve el permiso server-side; aca
    /// simulamos "no lo tiene" pasando el flag en false.)
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_WithoutPermission_RejectsPermInvariant_NoMutation()
    {
        var bundle = BuildService(debitNoteFlagOn: true);
        var (bcPublicId, bcId, _, _, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Estimated);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            bundle.Service.ConfirmPenaltyAsync(
                bcPublicId, PenaltyRequest(),
                "vendedor", "Vendedor", false, CancellationToken.None,
                userCanClassifyAgencyPenalty: false));
        Assert.Equal("INV-ADR014-PERM", ex.InvariantCode);

        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.NotEqual(PenaltyStatus.Confirmed, bcAfter.PenaltyStatus);
        Assert.Null(bcAfter.DebitNoteInvoiceId);
    }

    /// <summary>
    /// Sin soporte documental, confirm-penalty exige 4-eyes: tira ApprovalRequiredException (el
    /// controller -> 409 requiresApproval) y NO emite ND ni confirma la penalidad.
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_NoSupportingDocument_RequiresFourEyes_NoMutation()
    {
        var bundle = BuildService(debitNoteFlagOn: true);
        var (bcPublicId, bcId, _, _, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Estimated);

        // Sin soporte documental y sin approval valido -> 4-eyes.
        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            bundle.Service.ConfirmPenaltyAsync(
                bcPublicId, PenaltyRequest(support: null),
                "backoffice", "Back Office", false, CancellationToken.None,
                userCanClassifyAgencyPenalty: true));

        // El 4-eyes intercepta ANTES de confirmar/emitir: nada muto.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.NotEqual(PenaltyStatus.Confirmed, bcAfter.PenaltyStatus);
        Assert.Null(bcAfter.DebitNoteInvoiceId);
    }

    /// <summary>
    /// Monto sobre el umbral (<c>CancellationDebitNoteFourEyesThreshold</c>=2.000.000) exige
    /// 4-eyes aunque haya soporte documental.
    /// </summary>
    [Fact]
    public async Task ConfirmPenalty_AmountAboveThreshold_RequiresFourEyes_EvenWithSupport_NoMutation()
    {
        var bundle = BuildService(debitNoteFlagOn: true);
        // Factura original grande para que la penalidad sobre umbral no supere el total
        // (sino el gating la mandaria a manual por otro motivo, no por 4-eyes).
        var (bcPublicId, bcId, _, _, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Estimated,
            originalTotal: 10_000_000m);

        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            bundle.Service.ConfirmPenaltyAsync(
                bcPublicId, PenaltyRequest(amount: 3_000_000m, support: "https://docs/ok.pdf"),
                "backoffice", "Back Office", false, CancellationToken.None,
                userCanClassifyAgencyPenalty: true));

        // El 4-eyes intercepta ANTES de confirmar/emitir: nada muto (mismo invariante que sus
        // pares NoSupportingDocument/WithoutPermission — el umbral no debe dejar estado a medias).
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.NotEqual(PenaltyStatus.Confirmed, bcAfter.PenaltyStatus);
        Assert.Null(bcAfter.DebitNoteInvoiceId);
        Assert.Equal(DebitNoteStatus.NotApplicable, bcAfter.DebitNoteStatus);
    }

    // =========================================================================
    // Caso 11 (Dia 0, path SINCRONO) — el wiring de entrada sincrono tambien emite la ND.
    // ADR-013: cuando la penalidad propia YA quedo clasificada/confirmada en el Dia 0 (captura
    // sincrona de ConfirmAsync) y la NC total obtiene CAE, el callback OnArcaSucceededAsync
    // transiciona el BC y dispara TryEmit -> emite + vincula la ND. Es el complemento del flujo
    // DIFERIDO (caso 4): demuestra que el motor de la ND se alcanza por AMBOS caminos de entrada,
    // no solo por confirm-penalty.
    // =========================================================================

    /// <summary>
    /// Camino sincrono (Dia 0): un BC en <c>AwaitingFiscalConfirmation</c> con la penalidad propia
    /// ya clasificada y Confirmed (como la deja la captura sincrona de ConfirmAsync) emite + VINCULA
    /// la ND cuando llega el callback <c>OnArcaSucceededAsync</c> (la NC total obtuvo CAE). Cubre
    /// que el wiring de entrada SINCRONO tambien dispara la ND, no solo el diferido (caso 4).
    /// </summary>
    [Fact]
    public async Task OnArcaSucceeded_Day0_AgencyPenaltyAlreadyConfirmed_EmitsAndLinksRealDebitNote()
    {
        // ARRANGE — BC en AwaitingFiscalConfirmation (la NC aun NO tiene CAE: CreditNoteInvoiceId
        // null), con la penalidad propia YA clasificada+confirmada y su rastro de auditoria, como
        // la dejaria la captura sincrona del Dia 0. El gating de la ND exige todos esos campos.
        var bundle = BuildService(debitNoteFlagOn: true);
        var (_, bcId, originalInvoiceId, reservaId, _) = await SeedPostNcBcAsync(
            bundle.Ctx,
            supplierOwnership: PenaltyOwnership.Agency,
            conceptKind: CancellationConceptKind.AgencyManagementFee,
            penaltyStatus: PenaltyStatus.Confirmed,
            status: BookingCancellationStatus.AwaitingFiscalConfirmation,
            linkCreditNote: false,            // la NC se vincula en el callback, no antes
            penaltyAmountAtEvent: 30_000m,
            debitNotePurpose: DebitNotePurpose.PenaltyOrCancellationCharge,
            seedClassificationAudit: true);   // QUIEN clasifico/confirmo (invariante del gating B3)
        SetupCreateEmitsRealDebitNote(bundle, originalInvoiceId, reservaId);

        // La NC total que obtuvo CAE: el callback la vincula como CreditNoteInvoiceId del BC.
        // Necesitamos su Id para pasarlo a OnArcaSucceededAsync (segundo argumento).
        int creditNoteId;
        await using (var lookupCtx = _fixture.CreateDbContext())
        {
            creditNoteId = await lookupCtx.Invoices.AsNoTracking()
                .Where(i => i.TipoComprobante == 13 && i.OriginalInvoiceId == originalInvoiceId)
                .Select(i => i.Id)
                .FirstAsync();
        }

        // ACT — llega el callback post-CAE de la NC: transiciona el BC y dispara TryEmit (la ND).
        await bundle.Service.OnArcaSucceededAsync(originalInvoiceId, creditNoteId, CancellationToken.None);

        // ASSERT — el BC transiciono y la ND quedo emitida (Pending) + VINCULADA contra la BD real.
        await using var verifyCtx = _fixture.CreateDbContext();
        var bcAfter = await verifyCtx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);

        // El callback movio el BC a AwaitingOperatorRefund y vinculo la NC.
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcAfter.Status);
        Assert.Equal(creditNoteId, bcAfter.CreditNoteInvoiceId);

        // La ND se emitio por el path sincrono: Pending + vinculada + CbteTipo 12 (ND C).
        Assert.Equal(DebitNoteStatus.Pending, bcAfter.DebitNoteStatus);
        Assert.NotNull(bcAfter.DebitNoteInvoiceId);
        Assert.Equal(12, bcAfter.DebitNoteCbteTipoAtEvent);

        // Existe EXACTAMENTE una Invoice ND (12) para esa factura original.
        var ndCount = await verifyCtx.Invoices.AsNoTracking()
            .CountAsync(i => i.TipoComprobante == 12 && i.OriginalInvoiceId == originalInvoiceId);
        Assert.Equal(1, ndCount);
    }

    // =========================================================================
    // Caso 10 — CHECK constraint: el camino diferido nunca deja el FiscalSnapshot incoherente.
    // Probamos que un BC post-NC con snapshot INCOHERENTE no se puede ni siquiera sembrar
    // (lo rechaza chk_BookingCancellations_fiscalsnapshot_consistent). Esto blinda el invariante
    // sobre el que se apoya todo el flujo diferido (la NC ya salio => snapshot consistente).
    // =========================================================================

    /// <summary>
    /// Un BookingCancellation en estado post-NC con <c>FiscalSnapshot</c> incoherente (Source
    /// Unset, TC 0, Currency null) es RECHAZADO por el CHECK de Postgres
    /// <c>chk_BookingCancellations_fiscalsnapshot_consistent</c> al persistir. Garantiza que el
    /// flujo diferido nunca opera sobre un BC con snapshot invalido.
    /// </summary>
    [Fact]
    public async Task SeedingPostNcBcWithIncoherentFiscalSnapshot_IsRejectedByCheckConstraint()
    {
        await using var ctx = _fixture.CreateDbContext();
        var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        var bc = CancellationTestData.NewCancellation(custId, supId, resId, invId);
        bc.Status = BookingCancellationStatus.AwaitingOperatorRefund; // post-NC: exige snapshot coherente
        bc.FiscalSnapshot = CancellationTestData.IncompleteSnapshot(); // Source=Unset, TC=0, Currency=null

        ctx.BookingCancellations.Add(bc);

        // El CHECK SQL rebota con 23514, que el BusinessInvariantInterceptor mapea a
        // BusinessInvariantViolationException.
        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() => ctx.SaveChangesAsync());
    }
}
