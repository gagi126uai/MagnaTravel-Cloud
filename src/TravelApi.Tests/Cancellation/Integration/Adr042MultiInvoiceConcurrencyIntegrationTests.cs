using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-042 §7 c/d/e (B4 review, 2026-07-02): tests de CONCURRENCIA REAL contra Postgres del lock pesimista
/// (<c>SELECT ... FOR UPDATE</c>) que serializa los callbacks/retries del mismo <see cref="BookingCancellation"/>.
/// InMemory cortocircuita el lock (<c>!IsRelational()</c>), asi que la serializacion SOLO se puede validar aca.
///
/// <list type="bullet">
///   <item>(c) Dos callbacks de ARCA concurrentes (una NC cada uno) NO dejan el BC atascado en
///         AwaitingFiscalConfirmation con TODAS las hijas Succeeded (el lost-update de B1): el lock serializa la
///         lectura-decision de completitud y el BC termina AwaitingOperatorRefund.</item>
///   <item>(d) Dos retries concurrentes de la misma hija fallada -> UNA sola re-emision (el segundo, bajo el
///         lock, ve la hija ya Pending con job vivo -> no-op).</item>
///   <item>(e) Force de UNA sola NC en multi-factura NO cierra la anulacion (queda AwaitingFiscalConfirmation).</item>
/// </list>
///
/// <para><b>Requiere Docker</b> (Testcontainers Postgres, como el resto de la suite de integracion de
/// cancelacion). Si Docker no esta disponible, estos tests no corren.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr042MultiInvoiceConcurrencyIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr042MultiInvoiceConcurrencyIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Crea un service contra el ctx dado, con un invoice mock FIEL (marca AnnulmentStatus=Pending al
    /// encolar, via un ctx fresco, como el real) para que la deteccion de "job en vuelo" funcione.</summary>
    private BookingCancellationService BuildService(AppDbContext ctx)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        // Confirm y retry no llegan a ARCA aca (se driven callbacks/retries directos). El service escribe la
        // señal AnnulmentStatus=Pending bajo el lock (F1); los mocks solo devuelven completado.
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);
        invoiceMock
            .Setup(s => s.EnqueueAnnulmentRetryAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                EnableCancellationDebitNote = false,
                OperatorRefundTimeoutDays = 60,
                RequireApprovalForInvoiceAnnulment = false,
            });

        var approvalSettings = new Mock<IOperationalFinanceSettingsService>();
        approvalSettings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        var approvalService = new ApprovalRequestService(ctx, approvalSettings.Object);

        return new BookingCancellationService(
            ctx, invoiceMock.Object, approvalService, new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object, new Mock<IAdminUserCountService>().Object);
    }

    /// <summary>Siembra un BC AwaitingFiscalConfirmation con DOS facturas de venta (con CAE) y DOS hijas Pending.
    /// Devuelve (bcPublicId, invoice1Id, invoice2Id).</summary>
    private async Task<(Guid bcPublicId, int inv1Id, int inv2Id)> SeedMultiInvoiceBcAsync(
        BookingCancellationCreditNoteStatus child1Status = BookingCancellationCreditNoteStatus.Pending,
        BookingCancellationCreditNoteStatus child2Status = BookingCancellationCreditNoteStatus.Pending)
    {
        await using var ctx = _fixture.CreateDbContext();
        var (custId, supId, resId, inv1Id) = await CancellationTestData.SeedBaseAsync(ctx);

        // Segunda factura de venta (USD).
        var inv2 = new Invoice
        {
            TipoComprobante = 1, PuntoDeVenta = 1, NumeroComprobante = 2, ImporteTotal = 200m,
            ImporteNeto = 200m, ImporteIva = 0m, ReservaId = resId, MonId = "DOL", MonCotiz = 1000m,
            CAE = "cae-usd", Resultado = "A", CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(inv2);
        await ctx.SaveChangesAsync();

        var bc = CancellationTestData.NewCancellation(custId, supId, resId, inv1Id,
            BookingCancellationStatus.AwaitingFiscalConfirmation);
        bc.ConfirmedByUserId = "tester";
        bc.ConfirmedByUserName = "Tester";
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellationCreditNotes.Add(new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id, OriginatingInvoiceId = inv1Id, ArcaCurrency = "PES", Status = child1Status,
        });
        ctx.BookingCancellationCreditNotes.Add(new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id, OriginatingInvoiceId = inv2.Id, ArcaCurrency = "DOL", Status = child2Status,
        });
        await ctx.SaveChangesAsync();

        return (bc.PublicId, inv1Id, inv2.Id);
    }

    private async Task<int> AddCreditNoteAsync(int reservaId, int originalInvoiceId)
    {
        await using var ctx = _fixture.CreateDbContext();
        var nc = new Invoice
        {
            TipoComprobante = 3, PuntoDeVenta = 1, NumeroComprobante = 900 + originalInvoiceId,
            CAE = "cae-nc", Resultado = "A", ReservaId = reservaId, OriginalInvoiceId = originalInvoiceId,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(nc);
        await ctx.SaveChangesAsync();
        return nc.Id;
    }

    // (c) Dos callbacks concurrentes -> el lock serializa, BC termina AwaitingOperatorRefund (sin lost-update).
    [Fact]
    public async Task C_TwoConcurrentCallbacks_SerializedByLock_ClosesAwaitingOperatorRefund()
    {
        var (bcPublicId, inv1Id, inv2Id) = await SeedMultiInvoiceBcAsync();
        int reservaId, bcId;
        await using (var q = _fixture.CreateDbContext())
        {
            var bc = await q.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPublicId);
            bcId = bc.Id; reservaId = bc.ReservaId;
        }
        var nc1 = await AddCreditNoteAsync(reservaId, inv1Id);
        var nc2 = await AddCreditNoteAsync(reservaId, inv2Id);

        // Dos contexts + services independientes, callbacks en paralelo.
        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();
        var serviceA = BuildService(ctxA);
        var serviceB = BuildService(ctxB);

        await Task.WhenAll(
            serviceA.OnArcaSucceededAsync(inv1Id, nc1, CancellationToken.None),
            serviceB.OnArcaSucceededAsync(inv2Id, nc2, CancellationToken.None));

        // El lock evita el lost-update: con 0 Pending el BC quedo AwaitingOperatorRefund (no atascado).
        await using var verify = _fixture.CreateDbContext();
        var final = await verify.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, final.Status);
        Assert.NotNull(final.CreditNoteInvoiceId);
        var children = await verify.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.BookingCancellationId == bcId).ToListAsync();
        Assert.All(children, c => Assert.Equal(BookingCancellationCreditNoteStatus.Succeeded, c.Status));
    }

    // (d) Dos retries concurrentes de la misma hija fallada -> una sola re-emision (el segundo ve el job vivo).
    [Fact]
    public async Task D_TwoConcurrentRetries_OnlyOneReEmission()
    {
        // Una hija Succeeded (inv1) + una Failed (inv2). BC ArcaRejected.
        var (bcPublicId, inv1Id, inv2Id) = await SeedMultiInvoiceBcAsync(
            child1Status: BookingCancellationCreditNoteStatus.Succeeded,
            child2Status: BookingCancellationCreditNoteStatus.Failed);
        await using (var upd = _fixture.CreateDbContext())
        {
            var bc = await upd.BookingCancellations.FirstAsync(b => b.PublicId == bcPublicId);
            bc.Status = BookingCancellationStatus.ArcaRejected;
            await upd.SaveChangesAsync();
        }

        // Contamos las re-emisiones con un contador compartido (thread-safe) inyectado en ambos services via un
        // invoice mock compartido que ademas marca AnnulmentStatus=Pending (job vivo).
        int enqueueCount = 0;
        var gate = new object();
        var sharedInvoiceMock = new Mock<IInvoiceService>();
        sharedInvoiceMock
            .Setup(s => s.EnqueueAnnulmentAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Returns(Task.CompletedTask);
        // El retry (d) usa EnqueueAnnulmentRetryAsync. Contamos las re-emisiones aca. La deteccion de "job en
        // vuelo" NO depende de este mock: el service escribe AnnulmentStatus=Pending BAJO EL LOCK (F1), asi que
        // el segundo retry, serializado por el FOR UPDATE, lo ve y no re-encola. El conteo es determinista.
        sharedInvoiceMock
            .Setup(s => s.EnqueueAnnulmentRetryAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(() => { lock (gate) enqueueCount++; return Task.CompletedTask; });

        await using var ctxA = _fixture.CreateDbContext();
        await using var ctxB = _fixture.CreateDbContext();
        var serviceA = BuildServiceWith(ctxA, sharedInvoiceMock);
        var serviceB = BuildServiceWith(ctxB, sharedInvoiceMock);

        await Task.WhenAll(
            serviceA.RetryCreditNotesAsync(bcPublicId, "cajero-1", "Cajero", CancellationToken.None),
            serviceB.RetryCreditNotesAsync(bcPublicId, "cajero-2", "Cajero", CancellationToken.None));

        // La hija fallada se re-encolo UNA sola vez (el segundo retry, bajo el lock, vio la hija ya Pending
        // con job vivo -> no-op). inv1 (Succeeded) nunca se re-encola.
        Assert.Equal(1, enqueueCount);
    }

    // (e) Force de UNA sola NC en multi-factura NO cierra la anulacion.
    [Fact]
    public async Task E_ForceSingleNc_DoesNotClose_StaysAwaitingFiscalConfirmation()
    {
        var (bcPublicId, inv1Id, inv2Id) = await SeedMultiInvoiceBcAsync();
        int reservaId, bcId;
        await using (var q = _fixture.CreateDbContext())
        {
            var bc = await q.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPublicId);
            bcId = bc.Id; reservaId = bc.ReservaId;
        }
        var nc1 = await AddCreditNoteAsync(reservaId, inv1Id);

        Guid approvalPublicId;
        Guid ncPublicId;
        await using (var seed = _fixture.CreateDbContext())
        {
            ncPublicId = (await seed.Invoices.AsNoTracking().FirstAsync(i => i.Id == nc1)).PublicId;
            var approval = new ApprovalRequest
            {
                RequestType = ApprovalRequestType.InvariantOverride, EntityType = "BookingCancellation",
                EntityId = bcId, RequestedByUserId = "admin-1", RequestedAt = DateTime.UtcNow,
                Status = ApprovalStatus.Approved, ResolvedByUserId = "admin-1", ResolvedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7), Reason = "Force override multi-factura",
            };
            seed.ApprovalRequests.Add(approval);
            await seed.SaveChangesAsync();
            approvalPublicId = approval.PublicId;
        }

        await using var ctx = _fixture.CreateDbContext();
        var service = BuildService(ctx);
        var request = new TravelApi.Application.DTOs.ForceArcaConfirmationRequest(
            CreditNoteInvoicePublicId: ncPublicId,
            ApprovalRequestPublicId: approvalPublicId,
            Reason: "Forzar la NC 1 que salio fuera de banda, minimo veinte caracteres");

        await service.ForceArcaConfirmationAsync(bcPublicId, request, "admin-1", "Admin", CancellationToken.None);

        await using var verify = _fixture.CreateDbContext();
        var final = await verify.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bcId);
        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, final.Status);
        Assert.Null(final.CreditNoteInvoiceId);
    }

    private BookingCancellationService BuildServiceWith(AppDbContext ctx, Mock<IInvoiceService> invoiceMock)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true, EnableCancellationDebitNote = false,
                OperatorRefundTimeoutDays = 60, RequireApprovalForInvoiceAnnulment = false,
            });
        var approvalSettings = new Mock<IOperationalFinanceSettingsService>();
        approvalSettings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        return new BookingCancellationService(
            ctx, invoiceMock.Object, new ApprovalRequestService(ctx, approvalSettings.Object),
            new Mock<IAuditService>().Object, NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object, new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }
}
