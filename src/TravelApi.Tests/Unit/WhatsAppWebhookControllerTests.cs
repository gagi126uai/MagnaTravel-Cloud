using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class WhatsAppWebhookControllerTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new AppDbContext(options);
    }

    private static WebhooksController CreateController(
        AppDbContext db,
        ILeadService leadService,
        Mock<IWhatsAppDeliveryService> deliveryMock)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WhatsApp:WebhookSecret"] = "test-secret"
            })
            .Build();

        var webhookService = new WhatsAppWebhookService(leadService, deliveryMock.Object, db);
        var controller = new WebhooksController(
            leadService,
            webhookService,
            config,
            NullLogger<WebhooksController>.Instance,
            new Mock<IHttpClientFactory>().Object,
            new EntityReferenceResolver(db));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.ControllerContext.HttpContext.Request.Headers["X-Webhook-Secret"] = "test-secret";

        return controller;
    }

    [Fact]
    public async Task WhatsAppMessage_WithSkipLeadAutoCreation_DoesNotCreateLead()
    {
        await using var db = CreateDbContext();
        var leadService = new LeadService(db, new EntityReferenceResolver(db));
        var deliveryMock = new Mock<IWhatsAppDeliveryService>();
        deliveryMock
            .Setup(service => service.TryHandleIncomingOperationalMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var controller = CreateController(db, leadService, deliveryMock);

        var result = await controller.WhatsAppMessage(new WhatsAppMessageDto
        {
            Phone = "+5493364185078",
            Message = "Hola",
            Sender = "Cliente",
            SkipLeadAutoCreation = true
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);

        Assert.Contains("\"handledBy\":\"none\"", payload);
        Assert.Equal(0, await db.Leads.CountAsync());
        Assert.Equal(0, await db.LeadActivities.CountAsync());
    }

    [Fact]
    public async Task WhatsAppLead_WithExistingLead_UpdatesStructuredFields()
    {
        await using var db = CreateDbContext();
        db.Leads.Add(new Lead
        {
            FullName = "Consulta por WhatsApp (+5493364185078)",
            Phone = "+5493364185078",
            Source = "WhatsApp",
            Status = LeadStatus.New,
            Notes = "Lead creado automaticamente al recibir un mensaje sin proceso de bot completado."
        });
        await db.SaveChangesAsync();

        var leadService = new LeadService(db, new EntityReferenceResolver(db));
        var deliveryMock = new Mock<IWhatsAppDeliveryService>();
        var controller = CreateController(db, leadService, deliveryMock);

        var result = await controller.WhatsAppLead(new WhatsAppWebhookDto
        {
            Name = "Gaston Albornoz",
            Phone = "+5493364185078",
            Interest = "Brasil",
            Dates = "Octubre 2026",
            Travelers = "Familia de 4",
            Transcript = "[Cliente]: Hola\n[Bot]: Bienvenido"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        var lead = await db.Leads.SingleAsync();

        Assert.Contains("\"updated\":true", payload);
        Assert.Equal("Gaston Albornoz", lead.FullName);
        Assert.Equal("Brasil", lead.InterestedIn);
        Assert.Equal("Octubre 2026", lead.TravelDates);
        Assert.Equal("Familia de 4", lead.Travelers);
        Assert.Equal(1, await db.LeadActivities.CountAsync());
    }
}
