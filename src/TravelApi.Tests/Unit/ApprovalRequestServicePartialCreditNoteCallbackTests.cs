using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.4 (ADR-009 §2.7, 2026-05-21): tests unit puros del wiring
/// ApprovalRequestService -> IPartialCreditNoteApprovalBridge.
///
/// <para><b>Por que unit puros y no integration</b>: este modulo ya tuvo dos
/// sesiones donde la suite quedo colgada por <c>CustomWebApplicationFactory</c>
/// + <c>PostgresIntegrationFixture</c>. La regla operativa FC1.3 es: callbacks
/// y branching del service se validan con InMemory + Moq; los tests de schema
/// constraint (CHECK + xmin) quedan diferidos a una sesion QA dedicada con
/// <c>qa-automation-senior</c>.</para>
///
/// <para><b>Que cubren estos 5 tests</b>:
///   1. Approve PartialCreditNoteApproval -> bridge.OnApprovedAsync invocado.
///   2. Reject PartialCreditNoteApproval -> bridge.OnRejectedAsync invocado.
///   3. Approve con bridge que tira excepcion -> approval queda Approved + log
///      error (no se propaga al caller, no rollback).
///   4. Approve InvariantOverride (otro tipo) -> bridge NUNCA invocado.
///   5. Approve dos veces el mismo approval -> primera invoca, segunda no-op
///      idempotente y no invoca callback de nuevo.</para>
///
/// <para><b>Patron de mock del bridge</b>: como el service usa
/// <see cref="IServiceProvider"/> para resolver el bridge bajo demanda (rompe
/// el ciclo DI), aca mockeamos el provider para devolver el mock del bridge.
/// Equivalente a inyectar el bridge directo, pero respeta la signature real.</para>
/// </summary>
public class ApprovalRequestServicePartialCreditNoteCallbackTests
{
    // ============================================================
    // Helpers de armado
    // ============================================================

    /// <summary>
    /// Construye un AppDbContext InMemory con DB unica por test (xUnit corre en
    /// paralelo y un nombre compartido pisaria datos entre tests).
    /// </summary>
    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fc134-approval-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>
    /// Builder del service con todas las deps mockeadas. Devuelve los mocks
    /// para que cada test pueda configurar/verificar lo que necesite.
    /// </summary>
    private static (
        ApprovalRequestService Service,
        AppDbContext Ctx,
        Mock<IPartialCreditNoteApprovalBridge> BridgeMock,
        Mock<ILogger<ApprovalRequestService>> LoggerMock
    ) BuildService(bool registerBridge = true)
    {
        var ctx = NewDbContext();

        // Settings minimo necesario para Approve/Reject (expiration + cooldown).
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                ApprovalDefaultExpirationDays = 7,
                ApprovalRejectionCooldownHours = 1,
            });

        var bridgeMock = new Mock<IPartialCreditNoteApprovalBridge>();
        var loggerMock = new Mock<ILogger<ApprovalRequestService>>();

        // Mock del IServiceProvider que devuelve el bridge cuando se le pide.
        // Si registerBridge=false, devuelve null para simular un escenario sin
        // bridge registrado (ej. un standalone deployment).
        var spMock = new Mock<IServiceProvider>();
        spMock.Setup(sp => sp.GetService(typeof(IPartialCreditNoteApprovalBridge)))
            .Returns(registerBridge ? bridgeMock.Object : null!);

        var service = new ApprovalRequestService(
            ctx,
            settingsMock.Object,
            policyService: null,
            serviceProvider: spMock.Object,
            logger: loggerMock.Object);

        return (service, ctx, bridgeMock, loggerMock);
    }

    /// <summary>
    /// Inserta un <see cref="ApprovalRequest"/> en estado Pending y devuelve su
    /// PublicId para que el test invoque Approve/Reject sin tener que armarlo.
    /// </summary>
    private static async Task<Guid> SeedPendingApprovalAsync(
        AppDbContext ctx,
        ApprovalRequestType requestType,
        int entityId = 100,
        string requestedByUserId = "vendedor-1")
    {
        var ar = new ApprovalRequest
        {
            PublicId = Guid.NewGuid(),
            RequestType = requestType,
            EntityType = "BookingCancellation",
            EntityId = entityId,
            Status = ApprovalStatus.Pending,
            RequestedByUserId = requestedByUserId,
            RequestedByUserName = "Vendedor Test",
            RequestedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Reason = "Solicitud de prueba para callback FC1.3.4",
        };
        ctx.ApprovalRequests.Add(ar);
        await ctx.SaveChangesAsync();
        return ar.PublicId;
    }

    // ============================================================
    // 1) ApproveAsync con PartialCreditNoteApproval -> bridge invocado
    // ============================================================

    [Fact]
    public async Task ApproveAsync_PartialCreditNoteApproval_InvokesOnApprovedAsync()
    {
        var (svc, ctx, bridgeMock, _) = BuildService();
        var publicId = await SeedPendingApprovalAsync(ctx, ApprovalRequestType.PartialCreditNoteApproval);

        // Capturamos el Id (int) que va a recibir el bridge — lo necesitamos
        // para Verify y el bridge usa int (no Guid) por contrato.
        var approval = await ctx.ApprovalRequests.AsNoTracking().FirstAsync(a => a.PublicId == publicId);

        var dto = await svc.ApproveAsync(
            publicId: publicId,
            resolvedByUserId: "admin-1",
            resolvedByUserName: "Admin Uno",
            notes: "Aprobado segun criterio contador",
            ct: CancellationToken.None);

        // El bridge fue invocado exactamente 1 vez con los parametros correctos.
        bridgeMock.Verify(b => b.OnApprovedAsync(
            approval.Id,
            "admin-1",
            "Admin Uno",
            "Aprobado segun criterio contador",
            It.IsAny<CancellationToken>()),
            Times.Once);

        // El approval quedo Approved persistido.
        Assert.Equal(ApprovalStatus.Approved.ToString(), dto.Status);
        var refreshed = await ctx.ApprovalRequests.AsNoTracking().FirstAsync(a => a.PublicId == publicId);
        Assert.Equal(ApprovalStatus.Approved, refreshed.Status);
    }

    // ============================================================
    // 2) RejectAsync con PartialCreditNoteApproval -> bridge invocado
    // ============================================================

    [Fact]
    public async Task RejectAsync_PartialCreditNoteApproval_InvokesOnRejectedAsync()
    {
        var (svc, ctx, bridgeMock, _) = BuildService();
        var publicId = await SeedPendingApprovalAsync(ctx, ApprovalRequestType.PartialCreditNoteApproval);
        var approval = await ctx.ApprovalRequests.AsNoTracking().FirstAsync(a => a.PublicId == publicId);

        var dto = await svc.RejectAsync(
            publicId: publicId,
            resolvedByUserId: "admin-2",
            resolvedByUserName: "Admin Dos",
            notes: "Faltan items no reintegrables marcados",
            ct: CancellationToken.None);

        bridgeMock.Verify(b => b.OnRejectedAsync(
            approval.Id,
            "admin-2",
            "Admin Dos",
            "Faltan items no reintegrables marcados",
            It.IsAny<CancellationToken>()),
            Times.Once);

        // El approval quedo Rejected persistido + cooldown seteado.
        Assert.Equal(ApprovalStatus.Rejected.ToString(), dto.Status);
        var refreshed = await ctx.ApprovalRequests.AsNoTracking().FirstAsync(a => a.PublicId == publicId);
        Assert.Equal(ApprovalStatus.Rejected, refreshed.Status);
        Assert.NotNull(refreshed.CooldownUntil);
    }

    // ============================================================
    // 3) ApproveAsync: bridge throw -> approval queda Approved + log error
    // ============================================================

    [Fact]
    public async Task ApproveAsync_BridgeThrows_ApprovalStaysApprovedAndLogsError()
    {
        var (svc, ctx, bridgeMock, loggerMock) = BuildService();
        var publicId = await SeedPendingApprovalAsync(ctx, ApprovalRequestType.PartialCreditNoteApproval);

        // Configuramos el bridge para tirar — simula que el BC asociado esta
        // bloqueado o la BD se cayo. El service NO debe propagar la excepcion.
        bridgeMock.Setup(b => b.OnApprovedAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulado: BC bloqueado"));

        // No debe tirar al caller (la AR ya esta commiteada, el job reconcilia).
        var dto = await svc.ApproveAsync(publicId, "admin-1", "Admin", "Notas", CancellationToken.None);

        // La AR quedo en Approved a pesar del fallo del bridge.
        Assert.Equal(ApprovalStatus.Approved.ToString(), dto.Status);
        var refreshed = await ctx.ApprovalRequests.AsNoTracking().FirstAsync(a => a.PublicId == publicId);
        Assert.Equal(ApprovalStatus.Approved, refreshed.Status);

        // Verify que el logger capturo el error. Usamos It.IsAny<EventId> porque
        // no nos importa el EventId especifico, solo el LogLevel + que la
        // excepcion sea la nuestra.
        loggerMock.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<InvalidOperationException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ============================================================
    // 4) ApproveAsync con tipo != PartialCreditNoteApproval -> bridge NO invocado
    // ============================================================

    [Fact]
    public async Task ApproveAsync_NonPartialCreditNoteType_DoesNotInvokeBridge()
    {
        var (svc, ctx, bridgeMock, _) = BuildService();
        // Usamos InvariantOverride como representante de "otro tipo cualquiera".
        // El bridge no debe ser invocado para ningun tipo distinto a
        // PartialCreditNoteApproval — esa es la guarda del helper interno.
        var publicId = await SeedPendingApprovalAsync(ctx, ApprovalRequestType.InvariantOverride);

        await svc.ApproveAsync(publicId, "admin-1", "Admin", "Override admin", CancellationToken.None);

        // El bridge no fue invocado ni una sola vez para ningun tipo de parametro.
        bridgeMock.Verify(b => b.OnApprovedAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        bridgeMock.Verify(b => b.OnRejectedAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // 5) ApproveAsync dos veces -> bridge invocado UNA sola vez (idempotente)
    // ============================================================

    [Fact]
    public async Task ApproveAsync_PartialCreditNoteApproval_TwiceIsIdempotent_BridgeInvokedOnce()
    {
        // El contrato del service (lineas 118-119 ApprovalRequestService) es:
        // si la AR ya esta en Approved, retorna sin volver a persistir. Por
        // lo tanto el callback al bridge tampoco se vuelve a disparar — es
        // la guarda natural ante reintentos / doble click del admin.
        // Esto es importante porque el bridge ES idempotente, pero igual
        // queremos minimizar el ruido en logs / metricas.
        var (svc, ctx, bridgeMock, _) = BuildService();
        var publicId = await SeedPendingApprovalAsync(ctx, ApprovalRequestType.PartialCreditNoteApproval);

        await svc.ApproveAsync(publicId, "admin-1", "Admin", "Primera vez", CancellationToken.None);
        // Segunda llamada: el service ve Status=Approved y hace early return.
        await svc.ApproveAsync(publicId, "admin-1", "Admin", "Segunda vez (deberia no-op)", CancellationToken.None);

        // Bridge fue invocado solo en la primera llamada.
        bridgeMock.Verify(b => b.OnApprovedAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
