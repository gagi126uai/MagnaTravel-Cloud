using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using Xunit;

namespace TravelApi.Tests.Unit;

public class WhatsAppConversationsControllerTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetConversations_ReturnsLeadAndOperationalThreads()
    {
        await using var db = CreateDbContext();

        var lead = new Lead
        {
            FullName = "Gaston Albornoz",
            Phone = "+5493364185078",
            Status = LeadStatus.New,
            InterestedIn = "Brasil"
        };

        lead.Activities.Add(new LeadActivity
        {
            Type = "WhatsApp",
            CreatedBy = "WhatsApp Bot",
            Description = "Nueva conversacion con bot:\n[Cliente]: Hola\n[Bot]: Hola, soy el asistente."
        });

        var customer = new Customer
        {
            FullName = "Marisa Salafia",
            Phone = "+5493364000000"
        };

        var reserva = new Reserva
        {
            NumeroReserva = "F-2026-1011",
            Name = "Viaje Brasil",
            Status = EstadoReserva.Reserved,
            Payer = customer,
            SourceLeadId = 99
        };

        db.Leads.Add(lead);
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        db.WhatsAppDeliveries.Add(new WhatsAppDelivery
        {
            ReservaId = reserva.Id,
            Phone = "+5493364000000",
            Direction = WhatsAppDeliveryDirections.Outbound,
            Status = WhatsAppDeliveryStatuses.Sent,
            Kind = WhatsAppDeliveryKinds.Voucher,
            MessageText = "Te compartimos el voucher.",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            SentAt = DateTime.UtcNow.AddMinutes(-5)
        });

        await db.SaveChangesAsync();

        var controller = new WhatsAppConversationsController(db, new EntityReferenceResolver(db));
        var result = await controller.GetConversations(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var items = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<TravelApi.Application.DTOs.WhatsAppConversationListItemDto>>(ok.Value);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.ConversationType == "lead" && item.Title == "Gaston Albornoz");
        Assert.Contains(items, item => item.ConversationType == "operational" && item.Title == "Reserva F-2026-1011");
    }

    [Fact]
    public async Task GetConversationDetail_ForLead_ReturnsParsedTranscriptAndMessages()
    {
        await using var db = CreateDbContext();

        var lead = new Lead
        {
            PublicId = Guid.NewGuid(),
            FullName = "Gaston Albornoz",
            Phone = "+5493364185078",
            Status = LeadStatus.Contacted,
            InterestedIn = "Brasil",
            TravelDates = "Octubre 2026",
            Travelers = "Familia de 4"
        };

        lead.Activities.Add(new LeadActivity
        {
            Type = "WhatsApp",
            CreatedBy = "WhatsApp Bot",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            Description = "Conversacion capturada por bot:\n[Cliente]: Hola\n[Bot]: Hola, soy el asistente.\n[Cliente]: Brasil"
        });

        lead.Activities.Add(new LeadActivity
        {
            Type = "WhatsApp",
            CreatedBy = "Agente CRM",
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            Description = "Te paso opciones hoy."
        });

        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        var controller = new WhatsAppConversationsController(db, new EntityReferenceResolver(db));
        var result = await controller.GetConversationDetail("lead", lead.PublicId.ToString(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<TravelApi.Application.DTOs.WhatsAppConversationDetailDto>(ok.Value);

        Assert.Equal("lead", detail.ConversationType);
        Assert.Equal(4, detail.Messages.Count);
        Assert.Equal("client", detail.Messages[0].Sender);
        Assert.Equal("bot", detail.Messages[1].Sender);
        Assert.Equal("client", detail.Messages[2].Sender);
        Assert.Equal("agent", detail.Messages[3].Sender);
        Assert.Equal("Te paso opciones hoy.", detail.Messages[3].Text);
    }
}
