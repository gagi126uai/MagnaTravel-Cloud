using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.F2.6a (rehecho 2026-05-28): tests unit puros del
/// <see cref="PartialCreditNotePostingReconciliationJob"/> — la CAPA DE AGENDA.
///
/// <para><b>Que cambio respecto a la version rechazada</b>: el job ya NO consulta ARCA por su
/// cuenta. Delega la decision fiscal en <c>IInvoiceService.ReconcileStuckPartialCreditNoteAsync</c>.
/// Por eso estos tests mockean ESE metodo y verifican SOLO lo que es responsabilidad del job:
/// el discriminador de NC parcial, el rate-limit por <c>LastArcaAttemptAt</c>, el escalado a
/// manual (notificacion anti-spam), el early-return con el flag apagado, y el manejo M-2 (si el
/// service tira al sincronizar el BC, el job NO re-lanza). La logica fiscal real (que arregla
/// B-1/B-2/M-1) se prueba en <see cref="ReconcileStuckPartialCreditNoteServiceTests"/>.</para>
/// </summary>
public class PartialCreditNotePostingReconciliationJobTests
{
    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fc13-f26a-job-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (
        PartialCreditNotePostingReconciliationJob Job,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceServiceMock,
        Mock<INotificationService> NotificationMock,
        Mock<UserManager<ApplicationUser>> UserManagerMock,
        OperationalFinanceSettings Settings
    ) BuildJob(
        bool enablePartialCreditNotes = true,
        int stalenessMinutes = 10,
        int maxDaysBeforeAlert = 10)
    {
        var ctx = NewDbContext();

        var settings = new OperationalFinanceSettings
        {
            EnablePartialCreditNotes = enablePartialCreditNotes,
            IdempotencyKeyStaleThresholdMinutes = stalenessMinutes,
            ManualReviewMaxDaysBeforeRg4540Alert = maxDaysBeforeAlert,
            PartialCreditNoteRoundingTolerance = 0.01m,
        };

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var invoiceServiceMock = new Mock<IInvoiceService>();
        var notificationMock = new Mock<INotificationService>();

        var storeMock = new Mock<IUserStore<ApplicationUser>>();
        var userManagerMock = new Mock<UserManager<ApplicationUser>>(
            storeMock.Object,
            null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);

        var job = new PartialCreditNotePostingReconciliationJob(
            ctx,
            settingsMock.Object,
            invoiceServiceMock.Object,
            notificationMock.Object,
            userManagerMock.Object,
            NullLogger<PartialCreditNotePostingReconciliationJob>.Instance);

        return (job, ctx, invoiceServiceMock, notificationMock, userManagerMock, settings);
    }

    /// <summary>Crea la factura origen + su NC con el resultado/antiguedad dados.</summary>
    private static async Task<(Invoice Original, Invoice CreditNote)> SeedAsync(
        AppDbContext ctx,
        string ncResultado = "PENDING",
        decimal originalImporteTotal = 100_000m,
        decimal ncImporteTotal = 30_000m,
        DateTime? ncCreatedAt = null,
        DateTime? ncLastArcaAttemptAt = null,
        int originalId = 500,
        int ncId = 600,
        long originalNumeroComprobante = 1234,
        int reservaId = 700)
    {
        var reserva = new Reserva { Id = reservaId, NumeroReserva = $"R-2026-{reservaId}" };
        ctx.Reservas.Add(reserva);

        var original = new Invoice
        {
            Id = originalId,
            TipoComprobante = 6, // Factura B
            PuntoDeVenta = 7,
            NumeroComprobante = originalNumeroComprobante,
            ImporteTotal = originalImporteTotal,
            Resultado = "A",
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.Pending,
        };
        ctx.Invoices.Add(original);

        var creditNote = new Invoice
        {
            Id = ncId,
            TipoComprobante = 8, // NC B
            PuntoDeVenta = 7,
            NumeroComprobante = 0,
            ImporteTotal = ncImporteTotal,
            Resultado = ncResultado,
            ReservaId = reserva.Id,
            OriginalInvoiceId = original.Id,
            CreatedAt = ncCreatedAt ?? DateTime.UtcNow.AddMinutes(-30),
            LastArcaAttemptAt = ncLastArcaAttemptAt,
        };
        ctx.Invoices.Add(creditNote);

        await ctx.SaveChangesAsync();
        return (original, creditNote);
    }

    private static IList<ApplicationUser> AdminList(params string[] ids) =>
        ids.Select(id => new ApplicationUser { Id = id, UserName = $"admin-{id}" }).ToList();

    // =====================================================================================
    // 1) NC parcial colgada -> el job DELEGA en el service exactamente una vez.
    // =====================================================================================

    [Fact]
    public async Task RunAsync_PartialNcStuck_DelegatesToServiceReconcile()
    {
        var (job, ctx, invoiceServiceMock, _, _, _) = BuildJob();

        var (_, creditNote) = await SeedAsync(ctx, ncCreatedAt: DateTime.UtcNow.AddMinutes(-30));

        invoiceServiceMock
            .Setup(s => s.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.Confirmed));

        await job.RunAsync(CancellationToken.None);

        invoiceServiceMock.Verify(
            s => s.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // =====================================================================================
    // 2) NC TOTAL FC1.2 -> el job NO la toca (discriminador de monto), no delega.
    // =====================================================================================

    [Fact]
    public async Task RunAsync_DoesNotTouchFc12NcTotal()
    {
        var (job, ctx, invoiceServiceMock, _, _, _) = BuildJob();

        // NC TOTAL: ImporteTotal == ImporteTotal de la factura origen -> NO parcial.
        await SeedAsync(ctx, originalImporteTotal: 100_000m, ncImporteTotal: 100_000m,
            ncCreatedAt: DateTime.UtcNow.AddMinutes(-30));

        await job.RunAsync(CancellationToken.None);

        invoiceServiceMock.Verify(
            s => s.ReconcileStuckPartialCreditNoteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // =====================================================================================
    // 3) Flag apagado -> early return (no delega, no notifica).
    // =====================================================================================

    [Fact]
    public async Task RunAsync_FlagOff_NoOp()
    {
        var (job, ctx, invoiceServiceMock, notificationMock, _, _) =
            BuildJob(enablePartialCreditNotes: false);

        await SeedAsync(ctx, ncCreatedAt: DateTime.UtcNow.AddMinutes(-30));

        await job.RunAsync(CancellationToken.None);

        invoiceServiceMock.Verify(
            s => s.ReconcileStuckPartialCreditNoteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        notificationMock.Verify(
            n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // =====================================================================================
    // 4) Rate-limit: NC reconciliada hace poco -> no se vuelve a delegar esta corrida.
    // =====================================================================================

    [Fact]
    public async Task RunAsync_RecentlyAttempted_SkipsReconcile()
    {
        var (job, ctx, invoiceServiceMock, _, _, _) = BuildJob(stalenessMinutes: 10);

        // Colgada hace 30 min (entra a la ventana) PERO reconciliada hace 2 min (< 10 del
        // rate-limit) -> no se vuelve a delegar.
        await SeedAsync(ctx, ncCreatedAt: DateTime.UtcNow.AddMinutes(-30),
            ncLastArcaAttemptAt: DateTime.UtcNow.AddMinutes(-2));

        await job.RunAsync(CancellationToken.None);

        invoiceServiceMock.Verify(
            s => s.ReconcileStuckPartialCreditNoteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // =====================================================================================
    // 5) NeedsManualReview + dentro de la ventana de gracia -> NO notifica todavia.
    // =====================================================================================

    [Fact]
    public async Task RunAsync_NeedsManualReviewWithinGracePeriod_DoesNotNotify()
    {
        var (job, ctx, invoiceServiceMock, notificationMock, _, _) = BuildJob(maxDaysBeforeAlert: 10);

        // Colgada hace 30 min (dentro de la gracia de 10 dias).
        var (_, creditNote) = await SeedAsync(ctx, ncCreatedAt: DateTime.UtcNow.AddMinutes(-30));

        invoiceServiceMock
            .Setup(s => s.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.NeedsManualReview, "no se pudo correlacionar"));

        await job.RunAsync(CancellationToken.None);

        notificationMock.Verify(
            n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // =====================================================================================
    // 6) NeedsManualReview + supera la ventana de gracia -> notifica 1 vez/dia (anti-spam).
    // =====================================================================================

    [Fact]
    public async Task RunAsync_NeedsManualReviewBeyondGracePeriod_NotifiesAdminsOncePerDay()
    {
        var (job, ctx, invoiceServiceMock, notificationMock, userManagerMock, _) =
            BuildJob(maxDaysBeforeAlert: 10);

        // Colgada hace 15 dias (supera la gracia de 10).
        var (_, creditNote) = await SeedAsync(ctx, ncCreatedAt: DateTime.UtcNow.AddDays(-15));

        invoiceServiceMock
            .Setup(s => s.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.NeedsManualReview, "no se pudo correlacionar"));

        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync(AdminList("admin-1", "admin-2"));

        // Persistir las notifications en el ctx para que el dedup intra-dia funcione en la 2da corrida.
        notificationMock.Setup(n => n.CreateAndSendAsync(
                It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Returns<Notification, CancellationToken>(async (notif, token) =>
            {
                ctx.Notifications.Add(notif);
                await ctx.SaveChangesAsync(token);
                return notif;
            });

        // Dos corridas el mismo dia (simula el job cada 30 min). Reseteamos LastArcaAttemptAt
        // entre medio para que el rate-limit no saltee la 2da evaluacion (queremos probar el
        // dedup de NOTIFICACIONES, no el rate-limit).
        await job.RunAsync(CancellationToken.None);

        var ncMid = await ctx.Invoices.FirstAsync(i => i.Id == creditNote.Id);
        ncMid.LastArcaAttemptAt = null;
        await ctx.SaveChangesAsync();

        await job.RunAsync(CancellationToken.None);

        // 2 admins, dedup intra-dia -> exactamente 2 notifications (1 por admin), aunque corrio 2 veces.
        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.Is<Notification>(notif =>
                notif.RelatedEntityType == "PartialCreditNotePostingStuck"
                && notif.Priority == "Urgent"),
            It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // =====================================================================================
    // 7) ReEnqueuedEmission -> NO escala a manual (la NC esta en camino de re-emitirse).
    // =====================================================================================

    [Fact]
    public async Task RunAsync_ReEnqueuedEmission_DoesNotEscalateEvenIfOld()
    {
        var (job, ctx, invoiceServiceMock, notificationMock, userManagerMock, _) =
            BuildJob(maxDaysBeforeAlert: 10);

        // Colgada hace 15 dias (superaria la gracia) PERO el service la re-disparo -> NO escalar.
        var (_, creditNote) = await SeedAsync(ctx, ncCreatedAt: DateTime.UtcNow.AddDays(-15));

        invoiceServiceMock
            .Setup(s => s.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PartialCreditNotePostingReconcileResult(
                PartialCreditNotePostingReconcileOutcome.ReEnqueuedEmission, "re-disparada"));

        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin")).ReturnsAsync(AdminList("admin-1"));

        await job.RunAsync(CancellationToken.None);

        notificationMock.Verify(
            n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // =====================================================================================
    // 8) M-2: si el service tira (fallo del bridge tras confirmar) -> el job NO re-lanza,
    //    NO aborta la pasada, registra el error. La excepcion no se propaga fuera de RunAsync.
    // =====================================================================================

    [Fact]
    public async Task RunAsync_ServiceThrowsOnBridgeSync_JobSwallowsAndContinues()
    {
        var (job, ctx, invoiceServiceMock, _, _, _) = BuildJob();

        var (_, creditNote) = await SeedAsync(ctx, ncCreatedAt: DateTime.UtcNow.AddMinutes(-30));

        invoiceServiceMock
            .Setup(s => s.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bridge OnArcaSucceededAsync fallo (simulado)"));

        // No debe propagarse: el job captura el fallo y sigue (mismo ciclo lo re-detecta).
        var ex = await Record.ExceptionAsync(() => job.RunAsync(CancellationToken.None));
        Assert.Null(ex);
    }
}

/// <summary>
/// FC1.3.F2.6a (rehecho 2026-05-28): tests del CUERPO REAL de
/// <c>InvoiceService.ReconcileStuckPartialCreditNoteAsync</c> — la logica fiscal que arregla los
/// bugs de fondo de la revision (B-1, B-2, M-1).
///
/// <para><b>Por que estos tests son los importantes</b>: la version rechazada tenia tests
/// tautologicos que mockeaban <c>QueryLastAuthorizedWithDetailsAsync</c> con
/// <c>It.IsAny&lt;int?&gt;()</c> devolviendo <c>Found:true</c>, enmascarando que en produccion el
/// job pasaba <c>null</c> y ARCA devolvia SIEMPRE <c>Found:false</c>. Aca el mock del
/// <see cref="IAfipService"/> respeta el CONTRATO REAL: si <c>lastSeenNumeroBeforePost</c> es
/// <c>null</c> -> <c>Found:false</c>; si trae un numero real -> puede matchear.</para>
///
/// <para><b>InMemory + Moq</b>: misma regla operativa que el resto de FC1.3 — Postgres vive en el
/// VPS. El indice UNIQUE de <c>ArcaIdempotencyKeys</c> NO se valida con InMemory, pero estos tests
/// no dependen de el (controlamos las keys que sembramos). La integracion real queda para QA en VPS.</para>
/// </summary>
public class ReconcileStuckPartialCreditNoteServiceTests
{
    private const decimal Tolerance = 0.01m;
    private const int StaleMinutes = 10;

    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fc13-f26a-svc-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    /// <summary>
    /// Construye el service con AFIP mockeado de forma que respeta el CONTRATO REAL de
    /// <c>QueryLastAuthorizedWithDetailsAsync</c>: con <c>lastSeenNumeroBeforePost=null</c>
    /// devuelve <c>Found:false</c> (como AfipService.cs:1861); con un numero real, devuelve el
    /// <paramref name="arcaWhenNumberKnown"/> que le pasemos.
    /// </summary>
    private static (InvoiceService Service, AppDbContext Ctx, Mock<IAfipService> AfipMock, Mock<IInvoiceAnnulmentBcBridge> BridgeMock)
        BuildService(
            AppDbContext ctx,
            ArcaCompoundQueryResultFactory? arcaWhenNumberKnown = null,
            Action<int>? onProcessInvoiceJob = null)
    {
        var settings = new OperationalFinanceSettings
        {
            EnablePartialCreditNotes = true,
            EnablePartialCreditNoteRealEmission = true,
            PartialCreditNoteRoundingTolerance = Tolerance,
            IdempotencyKeyStaleThresholdMinutes = StaleMinutes,
        };
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var afipMock = new Mock<IAfipService>();

        // CONTRATO REAL: null -> Found:false; numero real -> lo que decida el factory.
        afipMock
            .Setup(a => a.QueryLastAuthorizedWithDetailsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pv, int tipo, int? lastSeen, CancellationToken _) =>
            {
                if (lastSeen is null)
                {
                    // Exactamente como AfipService.cs:1861: sin snapshot -> Found:false.
                    return new TravelApi.Application.DTOs.ArcaCompoundQueryResult(
                        Found: false, LastNumero: 0, Cae: null, CbteAsoc: null,
                        IssuedAt: null, ImporteTotal: null, MonId: null, MonCotiz: null);
                }
                return arcaWhenNumberKnown?.Invoke(pv, tipo, lastSeen.Value)
                    ?? new TravelApi.Application.DTOs.ArcaCompoundQueryResult(
                        Found: false, LastNumero: lastSeen.Value, Cae: null, CbteAsoc: null,
                        IssuedAt: null, ImporteTotal: null, MonId: null, MonCotiz: null);
            });

        afipMock
            .Setup(a => a.GetLastAuthorizedNumeroAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5000);

        afipMock
            .Setup(a => a.ProcessInvoiceJob(It.IsAny<int>()))
            .Returns((int invoiceId) =>
            {
                onProcessInvoiceJob?.Invoke(invoiceId);
                return Task.CompletedTask;
            });

        var bridgeMock = new Mock<IInvoiceAnnulmentBcBridge>();

        var mapper = new AutoMapper.MapperConfiguration(
            c => c.AddProfile<TravelApi.Application.Mappings.MappingProfile>()).CreateMapper();

        var service = new InvoiceService(
            ctx,
            new EntityReferenceResolver(ctx),
            afipMock.Object,
            new Mock<IInvoicePdfService>().Object,
            mapper,
            new Mock<IBackgroundJobClient>().Object,
            NullLogger<InvoiceService>.Instance,
            settingsMock.Object,
            BuildUserManager(),
            bcBridge: bridgeMock.Object);

        return (service, ctx, afipMock, bridgeMock);
    }

    private delegate TravelApi.Application.DTOs.ArcaCompoundQueryResult ArcaCompoundQueryResultFactory(
        int puntoVenta, int cbteTipo, int lastSeenNumero);

    /// <summary>
    /// Siembra factura origen (Factura B) + NC parcial PENDING + (opcional) su ArcaIdempotencyKey.
    /// La idemKey se calcula con la MISMA formula que el service re-deriva, asegurando que el
    /// lookup interno la encuentre.
    /// </summary>
    private static async Task<(Invoice Original, Invoice CreditNote)> SeedAsync(
        AppDbContext ctx,
        decimal originalTotal = 100_000m,
        decimal ncTotal = 30_000m,
        int approvalRequestId = 42,
        bool seedIdempotencyKey = true,
        int? keyLastSeenNumero = 4999,
        DateTime? keyCreatedAt = null,
        DateTime? keyResolvedAt = null,
        long originalNumeroComprobante = 1234,
        string ncMonId = "PES")
    {
        var reserva = new Reserva { Id = 700, NumeroReserva = "R-2026-700" };
        ctx.Reservas.Add(reserva);

        var original = new Invoice
        {
            Id = 500,
            TipoComprobante = 6, // Factura B
            PuntoDeVenta = 7,
            NumeroComprobante = originalNumeroComprobante,
            ImporteTotal = originalTotal,
            Resultado = "A",
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.Pending,
            AnnulmentApprovalRequestId = approvalRequestId,
        };
        ctx.Invoices.Add(original);

        var creditNote = new Invoice
        {
            Id = 600,
            TipoComprobante = 8, // NC B
            PuntoDeVenta = 7,
            NumeroComprobante = 0,
            ImporteTotal = ncTotal,
            MonId = ncMonId,
            Resultado = "PENDING",
            ReservaId = reserva.Id,
            OriginalInvoiceId = original.Id,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
        };
        ctx.Invoices.Add(creditNote);

        if (seedIdempotencyKey)
        {
            ctx.ArcaIdempotencyKeys.Add(new ArcaIdempotencyKey
            {
                Key = BuildExpectedIdemKey(original.Id, approvalRequestId, ncTotal, ncMonId),
                CreatedAt = keyCreatedAt ?? DateTime.UtcNow.AddMinutes(-30),
                ResolvedAt = keyResolvedAt,
                LastSeenNumeroBeforePost = keyLastSeenNumero,
            });
        }

        await ctx.SaveChangesAsync();
        return (original, creditNote);
    }

    /// <summary>
    /// Reproduce la formula de InvoiceService.BuildIdempotencyKey (SHA256 sobre
    /// "originalInvoiceId|approvalRequestId|monto:F2|ISO"), con el ISO derivado del MonId.
    /// </summary>
    private static string BuildExpectedIdemKey(int originalInvoiceId, int approvalRequestId, decimal fiscalAmount, string monId)
    {
        string iso = string.Equals(monId, "DOL", StringComparison.OrdinalIgnoreCase) ? "USD" : "ARS";
        string raw = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{originalInvoiceId}|{approvalRequestId}|{fiscalAmount:F2}|{iso}");
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // =====================================================================================
    // B-1: con la key huerfana, el service consulta ARCA con el numero REAL (no null) y
    //      confirma la NC. (En la version rechazada esto era inalcanzable en prod.)
    // =====================================================================================

    [Fact]
    public async Task Reconcile_OrphanKeyAndArcaConfirms_ConfirmsCreditNote()
    {
        var ctx = NewDbContext();
        // ARCA confirma SOLO cuando le llega un numero real (no null): el comprobante encontrado
        // apunta a la factura origen 1234 y su total = el de la NC.
        var (service, _, afipMock, bridgeMock) = BuildService(
            ctx,
            arcaWhenNumberKnown: (pv, tipo, lastSeen) =>
                new TravelApi.Application.DTOs.ArcaCompoundQueryResult(
                    Found: true, LastNumero: 5001, Cae: "75123456789012",
                    CbteAsoc: 1234, IssuedAt: DateTime.UtcNow.AddMinutes(-25),
                    ImporteTotal: 30_000m, MonId: "PES", MonCotiz: 1m));

        var (original, creditNote) = await SeedAsync(ctx, keyLastSeenNumero: 4999,
            keyCreatedAt: DateTime.UtcNow.AddMinutes(-30)); // vencida (> 10 min)

        var result = await service.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, CancellationToken.None);

        Assert.Equal(PartialCreditNotePostingReconcileOutcome.Confirmed, result.Outcome);

        var ncPost = await ctx.Invoices.AsNoTracking().FirstAsync(i => i.Id == creditNote.Id);
        Assert.Equal("A", ncPost.Resultado);
        Assert.Equal("75123456789012", ncPost.CAE);

        var origPost = await ctx.Invoices.AsNoTracking().FirstAsync(i => i.Id == original.Id);
        Assert.Equal(AnnulmentStatus.Succeeded, origPost.AnnulmentStatus);

        // Prueba dura de B-1: ARCA fue consultado con un numero REAL (4999), nunca con null.
        afipMock.Verify(a => a.QueryLastAuthorizedWithDetailsAsync(
            7, 8, 4999, It.IsAny<CancellationToken>()), Times.Once);

        bridgeMock.Verify(b => b.OnArcaSucceededAsync(
            original.Id, creditNote.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    // =====================================================================================
    // B-2 (falso match): ARCA tiene un comprobante del MISMO MONTO pero asociado a OTRA factura
    //      (CbteAsoc != NumeroComprobante de nuestra origen) -> NO confirma con el CAE ajeno.
    // =====================================================================================

    [Fact]
    public async Task Reconcile_ArcaCbteAsocPointsToDifferentInvoice_DoesNotConfirmWrongCae()
    {
        var ctx = NewDbContext();
        // ARCA devuelve un comprobante del mismo monto (30000) pero CbteAsoc = 9999 (OTRA factura),
        // no 1234. El match por comprobante asociado debe FALLAR aunque el monto coincida.
        var (service, _, _, bridgeMock) = BuildService(
            ctx,
            arcaWhenNumberKnown: (pv, tipo, lastSeen) =>
                new TravelApi.Application.DTOs.ArcaCompoundQueryResult(
                    Found: true, LastNumero: 5001, Cae: "75999999999999",
                    CbteAsoc: 9999, IssuedAt: DateTime.UtcNow, // <-- factura ajena
                    ImporteTotal: 30_000m, MonId: "PES", MonCotiz: 1m));

        var (original, creditNote) = await SeedAsync(ctx, originalNumeroComprobante: 1234,
            keyLastSeenNumero: 4999, keyCreatedAt: DateTime.UtcNow.AddMinutes(-30));

        var result = await service.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, CancellationToken.None);

        // NO se confirmo con el CAE equivocado: el arbitro borro la key huerfana y re-disparo.
        Assert.Equal(PartialCreditNotePostingReconcileOutcome.ReEnqueuedEmission, result.Outcome);

        var ncPost = await ctx.Invoices.AsNoTracking().FirstAsync(i => i.Id == creditNote.Id);
        Assert.NotEqual("75999999999999", ncPost.CAE); // NUNCA se le pego el CAE ajeno
        Assert.NotEqual("A", ncPost.Resultado);          // no quedo aprobada con el CAE de otra NC

        // El bridge NO se invoco con un CAE incorrecto.
        bridgeMock.Verify(b => b.OnArcaSucceededAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =====================================================================================
    // M-1 (race): la key NO esta vencida (emisor en vuelo) -> el service NO toca nada,
    //      ni consulta ARCA, ni confirma.
    // =====================================================================================

    [Fact]
    public async Task Reconcile_KeyNotStale_ReturnsInFlightAndDoesNotTouchArca()
    {
        var ctx = NewDbContext();
        var (service, _, afipMock, bridgeMock) = BuildService(ctx);

        // Key creada hace 2 min (< 10 del umbral) -> emisor en vuelo.
        var (_, creditNote) = await SeedAsync(ctx,
            keyCreatedAt: DateTime.UtcNow.AddMinutes(-2), keyLastSeenNumero: 4999);

        var result = await service.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, CancellationToken.None);

        Assert.Equal(PartialCreditNotePostingReconcileOutcome.InFlight, result.Outcome);

        // NO consultamos ARCA ni re-POSTeamos: no pisamos al emisor.
        afipMock.Verify(a => a.QueryLastAuthorizedWithDetailsAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        afipMock.Verify(a => a.ProcessInvoiceJob(It.IsAny<int>()), Times.Never);
        bridgeMock.Verify(b => b.OnArcaSucceededAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        var ncPost = await ctx.Invoices.AsNoTracking().FirstAsync(i => i.Id == creditNote.Id);
        Assert.Equal("PENDING", ncPost.Resultado);
    }

    // =====================================================================================
    // NC sin ArcaIdempotencyKey -> re-dispara la emision idempotente, NO confirma.
    // =====================================================================================

    [Fact]
    public async Task Reconcile_NoIdempotencyKey_ReEnqueuesEmission()
    {
        var ctx = NewDbContext();
        bool processInvoiceCalled = false;
        var (service, _, afipMock, bridgeMock) = BuildService(
            ctx,
            onProcessInvoiceJob: _ => processInvoiceCalled = true);

        var (_, creditNote) = await SeedAsync(ctx, seedIdempotencyKey: false);

        var result = await service.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, CancellationToken.None);

        Assert.Equal(PartialCreditNotePostingReconcileOutcome.ReEnqueuedEmission, result.Outcome);

        // Re-disparo: re-armo una key fresca (snapshot del numerador) + re-POSTeo la NC ya creada.
        afipMock.Verify(a => a.GetLastAuthorizedNumeroAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(processInvoiceCalled, "Debio re-POSTear la NC ya creada (ProcessInvoiceJob).");

        // NUNCA confirmo a ciegas.
        bridgeMock.Verify(b => b.OnArcaSucceededAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =====================================================================================
    // Sin AnnulmentApprovalRequestId en la factura origen -> NeedsManualReview (no adivina).
    // =====================================================================================

    [Fact]
    public async Task Reconcile_OriginalInvoiceWithoutApproval_NeedsManualReview()
    {
        var ctx = NewDbContext();
        var (service, _, afipMock, _) = BuildService(ctx);

        // approvalRequestId 0 + lo limpiamos: simula factura origen sin approval vinculado.
        var (original, creditNote) = await SeedAsync(ctx, seedIdempotencyKey: false);
        var orig = await ctx.Invoices.FirstAsync(i => i.Id == original.Id);
        orig.AnnulmentApprovalRequestId = null;
        await ctx.SaveChangesAsync();

        var result = await service.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, CancellationToken.None);

        Assert.Equal(PartialCreditNotePostingReconcileOutcome.NeedsManualReview, result.Outcome);

        // No re-POSTea ni consulta ARCA: necesita ojo humano.
        afipMock.Verify(a => a.ProcessInvoiceJob(It.IsAny<int>()), Times.Never);
        afipMock.Verify(a => a.QueryLastAuthorizedWithDetailsAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =====================================================================================
    // M-2: cuando ARCA confirma pero el bridge tira al sincronizar el BC -> el service
    //      PROPAGA la excepcion (no la traga), para que el job la registre y re-detecte.
    // =====================================================================================

    [Fact]
    public async Task Reconcile_BridgeThrowsAfterConfirm_Propagates()
    {
        var ctx = NewDbContext();
        var (service, _, _, bridgeMock) = BuildService(
            ctx,
            arcaWhenNumberKnown: (pv, tipo, lastSeen) =>
                new TravelApi.Application.DTOs.ArcaCompoundQueryResult(
                    Found: true, LastNumero: 5001, Cae: "75123456789012",
                    CbteAsoc: 1234, IssuedAt: DateTime.UtcNow,
                    ImporteTotal: 30_000m, MonId: "PES", MonCotiz: 1m));

        bridgeMock
            .Setup(b => b.OnArcaSucceededAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BC bridge fallo (simulado)"));

        var (_, creditNote) = await SeedAsync(ctx, keyCreatedAt: DateTime.UtcNow.AddMinutes(-30),
            keyLastSeenNumero: 4999);

        // El service NO traga el fallo del bridge: propaga (M-2 fix). El job lo captura.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, CancellationToken.None));

        // La NC SI quedo confirmada (la confirmacion fiscal corre antes del bridge).
        var ncPost = await ctx.Invoices.AsNoTracking().FirstAsync(i => i.Id == creditNote.Id);
        Assert.Equal("A", ncPost.Resultado);
    }
}
