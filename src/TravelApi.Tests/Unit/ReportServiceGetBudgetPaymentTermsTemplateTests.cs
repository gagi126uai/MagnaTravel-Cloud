using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Fix bloqueante (2026-08-13, hallazgo de frontend-reviewer): GET budget-payment-terms-template debe
/// devolver SOLO el texto de la plantilla (nunca la entidad AgencySettings completa) y nunca romper si
/// todavía no hay fila de configuración cargada — la ficha de reserva necesita poder precargar el
/// textarea sin depender del permiso Admin-only de Configuración.
/// </summary>
public class ReportServiceGetBudgetPaymentTermsTemplateTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IBnaExchangeRateService> _bnaMock;

    public ReportServiceGetBudgetPaymentTermsTemplateTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _bnaMock = new Mock<IBnaExchangeRateService>();
    }

    private ReportService BuildService(AppDbContext context) =>
        new(context, _bnaMock.Object);

    [Fact]
    public async Task SinFilaDeConfiguracion_DevuelveTextoNull()
    {
        await using var context = new AppDbContext(_dbOptions);
        var service = BuildService(context);

        var result = await service.GetBudgetPaymentTermsTemplateAsync(CancellationToken.None);

        Assert.Null(result.Text);
    }

    [Fact]
    public async Task ConPlantillaCargada_DevuelveElTexto()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.AgencySettings.Add(new AgencySettings
        {
            AgencyName = "Agencia Test",
            Currency = "ARS",
            BudgetPaymentTermsTemplate = "Seña del 30% al confirmar, saldo 15 días antes del viaje."
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);
        var result = await service.GetBudgetPaymentTermsTemplateAsync(CancellationToken.None);

        Assert.Equal("Seña del 30% al confirmar, saldo 15 días antes del viaje.", result.Text);
    }

    [Fact]
    public async Task ConFilaDeConfiguracionSinPlantilla_DevuelveTextoNull()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.AgencySettings.Add(new AgencySettings
        {
            AgencyName = "Agencia Test",
            Currency = "ARS",
            BudgetPaymentTermsTemplate = null
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);
        var result = await service.GetBudgetPaymentTermsTemplateAsync(CancellationToken.None);

        Assert.Null(result.Text);
    }
}
