using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Ai;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Mini-tanda PDF-2a (2026-08-12): el borrador de condiciones del presupuesto redactado por IA
/// ("✨ Ayudame a redactarlo"). Regla P-21 — el borrador NUNCA se guarda solo — y regla de
/// data-exposure — el mensaje que ve el vendedor cuando la IA falla es SIEMPRE el mismo texto en
/// criollo, nunca el motivo técnico interno.
/// </summary>
public class ReportServiceBudgetConditionDraftTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IBnaExchangeRateService> _bnaMock;

    private const string AiUnavailableMessage =
        "La inteligencia artificial no está disponible ahora. Escribí el texto a mano o probá más tarde.";

    public ReportServiceBudgetConditionDraftTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _bnaMock = new Mock<IBnaExchangeRateService>();
    }

    private ReportService BuildService(
        IAiAssistantService? aiAssistantService, IAiConnectionResolver? aiConnectionResolver, AppDbContext context)
    {
        return new ReportService(
            context,
            _bnaMock.Object,
            permissionResolver: null,
            httpContextAccessor: null,
            financePositionService: null,
            exchangeRateResolver: null,
            logger: null,
            fileStoragePort: null,
            aiAssistantService: aiAssistantService,
            aiConnectionResolver: aiConnectionResolver);
    }

    [Fact]
    public async Task KindInvalido_Rechaza_SinConsultarLaIa()
    {
        await using var context = new AppDbContext(_dbOptions);
        var assistantMock = new Mock<IAiAssistantService>();
        var resolverMock = new Mock<IAiConnectionResolver>();
        var service = BuildService(assistantMock.Object, resolverMock.Object, context);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GenerateBudgetConditionDraftAsync("CategoriaQueNoExiste", null, CancellationToken.None));

        // El kind se valida ANTES de gastar nada con la IA.
        resolverMock.Verify(r => r.IsUsableAsync(It.IsAny<CancellationToken>()), Times.Never);
        assistantMock.Verify(
            a => a.CompleteAsync(It.IsAny<AiChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IaNoUsable_MensajeCriollo_NoLlamaAlModelo()
    {
        await using var context = new AppDbContext(_dbOptions);
        var assistantMock = new Mock<IAiAssistantService>();
        var resolverMock = new Mock<IAiConnectionResolver>();
        resolverMock.Setup(r => r.IsUsableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var service = BuildService(assistantMock.Object, resolverMock.Object, context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateBudgetConditionDraftAsync(BudgetConditionBlockKindText.Hotels, null, CancellationToken.None));

        Assert.Equal(AiUnavailableMessage, ex.Message);
        assistantMock.Verify(
            a => a.CompleteAsync(It.IsAny<AiChatRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IaDegrada_MensajeCriollo_SinDetalleTecnico()
    {
        await using var context = new AppDbContext(_dbOptions);
        var resolverMock = new Mock<IAiConnectionResolver>();
        resolverMock.Setup(r => r.IsUsableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var assistantMock = new Mock<IAiAssistantService>();
        assistantMock
            .Setup(a => a.CompleteAsync(It.IsAny<AiChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AiChatResult.Degraded("timeout del proveedor (detalle tecnico interno)"));

        var service = BuildService(assistantMock.Object, resolverMock.Object, context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateBudgetConditionDraftAsync(BudgetConditionBlockKindText.Transfers, null, CancellationToken.None));

        // El mensaje al usuario es SIEMPRE el mismo texto en criollo: nunca el DegradationReason interno.
        Assert.Equal(AiUnavailableMessage, ex.Message);
    }

    [Fact]
    public async Task Exito_DevuelveElTexto_YNoPersisteNada()
    {
        await using var context = new AppDbContext(_dbOptions);
        var resolverMock = new Mock<IAiConnectionResolver>();
        resolverMock.Setup(r => r.IsUsableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        const string draftedText = "Tarifas sujetas a disponibilidad y cambio sin previo aviso...";
        var assistantMock = new Mock<IAiAssistantService>();
        assistantMock
            .Setup(a => a.CompleteAsync(It.IsAny<AiChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AiChatResult.Success(draftedText));

        var service = BuildService(assistantMock.Object, resolverMock.Object, context);

        var draft = await service.GenerateBudgetConditionDraftAsync(
            BudgetConditionBlockKindText.General, currentText: null, CancellationToken.None);

        Assert.Equal(draftedText, draft.Text);

        // El borrador NUNCA se guarda solo (regla P-21): la tabla de bloques tiene que seguir vacía.
        Assert.Empty(await context.Set<BudgetConditionBlock>().ToListAsync());
    }

    [Fact]
    public async Task ConTextoDeBase_LoManda_EnElPedidoAlModelo()
    {
        await using var context = new AppDbContext(_dbOptions);
        var resolverMock = new Mock<IAiConnectionResolver>();
        resolverMock.Setup(r => r.IsUsableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        AiChatRequest? capturedRequest = null;
        var assistantMock = new Mock<IAiAssistantService>();
        assistantMock
            .Setup(a => a.CompleteAsync(It.IsAny<AiChatRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AiChatRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(AiChatResult.Success("texto mejorado"));

        var service = BuildService(assistantMock.Object, resolverMock.Object, context);

        await service.GenerateBudgetConditionDraftAsync(
            BudgetConditionBlockKindText.Packages, currentText: "seña del 30% no reembolsable", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Contains(capturedRequest!.Messages, m => m.Content.Contains("seña del 30% no reembolsable", StringComparison.Ordinal));
    }
}
