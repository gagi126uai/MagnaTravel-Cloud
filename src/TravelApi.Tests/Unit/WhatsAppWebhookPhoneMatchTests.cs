using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Compatibilidad del match de telefono del webhook de WhatsApp tras unificar la normalizacion
/// con PhoneNormalizer (solo digitos). Antes el match era laxo (solo sacaba '+'); estos tests
/// verifican que los formatos que matcheaban antes SIGUEN matcheando, y que ahora ademas cubre
/// formatos con guiones/parentesis que antes se escapaban.
/// </summary>
public class WhatsAppWebhookPhoneMatchTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static WhatsAppWebhookService NewService(AppDbContext ctx)
    {
        // LeadService real: el webhook lo usa para crear leads / agregar actividades.
        var resolver = new Mock<IEntityReferenceResolver>();
        var leadService = new LeadService(ctx, resolver.Object);
        var delivery = new Mock<IWhatsAppDeliveryService>();
        delivery.Setup(d => d.TryHandleIncomingOperationalMessageAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        return new WhatsAppWebhookService(leadService, delivery.Object, ctx);
    }

    [Fact]
    public async Task ProcessLeadCapture_MatchesExistingLead_WhenStoredWithPlusPrefix()
    {
        // Compat clasico: el lead viejo se guardo con '+'; el bot llega con el mismo numero sin '+'.
        await using var ctx = NewContext();
        ctx.Leads.Add(new Lead { FullName = "Juan", Phone = "+5491155555555", Status = LeadStatus.Contacted });
        await ctx.SaveChangesAsync();

        var service = NewService(ctx);
        var result = await service.ProcessLeadCaptureAsync(
            new WhatsAppWebhookDto { Name = "Juan", Phone = "5491155555555" },
            CancellationToken.None);

        Assert.False(result.Created);            // matcheo el existente, no creo uno nuevo
        Assert.Equal(1, await ctx.Leads.CountAsync());
    }

    [Fact]
    public async Task ProcessLeadCapture_MatchesExistingLead_WhenStoredWithDashesAndSpaces()
    {
        // Caso NUEVO que el match laxo viejo NO cubria (guiones/espacios): ahora SI matchea.
        await using var ctx = NewContext();
        ctx.Leads.Add(new Lead { FullName = "Maria", Phone = "+54 9 11 5555-5555", Status = LeadStatus.New });
        await ctx.SaveChangesAsync();

        var service = NewService(ctx);
        var result = await service.ProcessLeadCaptureAsync(
            new WhatsAppWebhookDto { Name = "Maria", Phone = "5491155555555" },
            CancellationToken.None);

        Assert.False(result.Created);
        Assert.Equal(1, await ctx.Leads.CountAsync());
    }

    [Fact]
    public async Task ProcessLeadCapture_CreatesNewLead_WhenNoPhoneMatch()
    {
        await using var ctx = NewContext();
        ctx.Leads.Add(new Lead { FullName = "Otro", Phone = "+5491100000000", Status = LeadStatus.New });
        await ctx.SaveChangesAsync();

        var service = NewService(ctx);
        var result = await service.ProcessLeadCaptureAsync(
            new WhatsAppWebhookDto { Name = "Nuevo", Phone = "5491199999999" },
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(2, await ctx.Leads.CountAsync());
    }
}
