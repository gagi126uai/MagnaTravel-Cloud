using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Ai;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// TANDA 4 (2026-08-13): el borrador de la plantilla de "Formas de pago" de Configuración redactado
/// por IA ("✨ Ayudame a redactarlo"). Gemelo de <c>ReportServiceBudgetConditionDraftTests</c> pero sin
/// categoría. Regla P-21 — el borrador NUNCA se guarda solo — y regla de data-exposure — el mensaje que
/// ve el dueño cuando la IA falla es SIEMPRE el mismo texto en criollo, nunca el motivo técnico interno.
/// </summary>
public class ReportServiceBudgetPaymentTermsTemplateDraftTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IBnaExchangeRateService> _bnaMock;

    private const string AiUnavailableMessage =
        "La inteligencia artificial no está disponible ahora. Escribí el texto a mano o probá más tarde.";

    public ReportServiceBudgetPaymentTermsTemplateDraftTests()
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
    public async Task IaNoUsable_MensajeCriollo_NoLlamaAlModelo()
    {
        await using var context = new AppDbContext(_dbOptions);
        var assistantMock = new Mock<IAiAssistantService>();
        var resolverMock = new Mock<IAiConnectionResolver>();
        resolverMock.Setup(r => r.IsUsableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var service = BuildService(assistantMock.Object, resolverMock.Object, context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateBudgetPaymentTermsTemplateDraftAsync(null, CancellationToken.None));

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
            service.GenerateBudgetPaymentTermsTemplateDraftAsync(null, CancellationToken.None));

        // El mensaje al usuario es SIEMPRE el mismo texto en criollo: nunca el DegradationReason interno.
        Assert.Equal(AiUnavailableMessage, ex.Message);
    }

    [Fact]
    public async Task Exito_DevuelveElTexto_YNoPersisteNada()
    {
        await using var context = new AppDbContext(_dbOptions);
        var resolverMock = new Mock<IAiConnectionResolver>();
        resolverMock.Setup(r => r.IsUsableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        const string draftedText = "Seña del 30% al confirmar y saldo 15 días antes del viaje...";
        var assistantMock = new Mock<IAiAssistantService>();
        assistantMock
            .Setup(a => a.CompleteAsync(It.IsAny<AiChatRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AiChatResult.Success(draftedText));

        var service = BuildService(assistantMock.Object, resolverMock.Object, context);

        var draft = await service.GenerateBudgetPaymentTermsTemplateDraftAsync(null, CancellationToken.None);

        Assert.Equal(draftedText, draft.Text);

        // El borrador NUNCA se guarda solo (regla P-21): la configuracion de la agencia sigue sin fila.
        Assert.Null(await context.AgencySettings.FirstOrDefaultAsync());
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

        await service.GenerateBudgetPaymentTermsTemplateDraftAsync(
            currentText: "seña del 30% no reembolsable", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Contains(capturedRequest!.Messages, m => m.Content.Contains("seña del 30% no reembolsable", StringComparison.Ordinal));
    }
}
