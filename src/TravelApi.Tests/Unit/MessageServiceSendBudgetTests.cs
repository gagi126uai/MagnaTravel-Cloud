using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// TANDA 4 (2026-08-13): envío del PDF de PRESUPUESTO al cliente por WhatsApp
/// (MessageService.SendBudgetMessageAsync). Cubre: reserva inexistente, cliente sin teléfono, reserva
/// fuera de etapa Presupuesto (el error lo propaga IReservaService.GetBudgetPdfAsync tal cual), y el
/// happy path que registra la entrega como MessageDelivery (Kind = "Budget") reusando el generador de
/// PDF ya existente.
/// </summary>
public class MessageServiceSendBudgetTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public MessageServiceSendBudgetTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    // Actor Admin: saltea los gates de permiso/ownership para que los tests se concentren en las reglas
    // especificas del envio de presupuesto (contacto, etapa, registro de la entrega).
    private static OperationActor AdminActor() =>
        new("admin-1", "Admin Test", new[] { "Admin" });

    private static Customer BuildCustomerWithPhone(int id, string? phone) =>
        new() { Id = id, PublicId = Guid.NewGuid(), FullName = "Cliente Test", Phone = phone };

    private static Reserva BuildReserva(int id, Customer payer, string status = EstadoReserva.Budget) =>
        new()
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            NumeroReserva = $"2026-{id:000}",
            Name = "Reserva test",
            Status = status,
            PayerId = payer.Id,
            Payer = payer
        };

    private static (MessageService Service, Mock<IWhatsAppGateway> Gateway, Mock<IReservaService> ReservaService)
        BuildService(AppDbContext context)
    {
        var gateway = new Mock<IWhatsAppGateway>();
        gateway.Setup(g => g.SendDocumentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WhatsAppSendResult(true, "wamid-123", null));

        var reservaService = new Mock<IReservaService>();

        var service = new MessageService(
            context,
            gateway.Object,
            new Mock<IVoucherService>().Object,
            new Mock<IInvoiceService>().Object,
            reservaService.Object);

        return (service, gateway, reservaService);
    }

    [Fact]
    public async Task NonExistentReserva_Throws()
    {
        using var context = new AppDbContext(_dbOptions);
        var (service, _, _) = BuildService(context);

        var request = new SendBudgetMessageRequest { ReservaId = Guid.NewGuid().ToString() };

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.SendBudgetMessageAsync(request, AdminActor(), CancellationToken.None));
    }

    [Fact]
    public async Task CustomerWithoutPhone_ThrowsClearMessageWithCustomerName()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, null); // sin telefono cargado
        var reserva = BuildReserva(1, payer);
        context.Customers.Add(payer);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var (service, gateway, reservaService) = BuildService(context);
        var request = new SendBudgetMessageRequest { ReservaId = reserva.PublicId.ToString() };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendBudgetMessageAsync(request, AdminActor(), CancellationToken.None));

        Assert.Contains("Cliente Test", ex.Message);
        Assert.Contains("teléfono", ex.Message);

        // No se llego a pedir el PDF ni a mandar nada.
        reservaService.Verify(
            s => s.GetBudgetPdfAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        gateway.Verify(g => g.SendDocumentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReservaOutsideBudgetStage_PropagatesErrorFromPdfService()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        // Ya aceptada: el PDF de presupuesto ya no corresponde (regla de IReservaService.GetBudgetPdfAsync).
        var reserva = BuildReserva(1, payer, EstadoReserva.Confirmed);
        context.Customers.Add(payer);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var (service, gateway, reservaService) = BuildService(context);
        reservaService
            .Setup(s => s.GetBudgetPdfAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "El PDF de presupuesto solo se emite mientras el documento es un presupuesto."));

        var request = new SendBudgetMessageRequest { ReservaId = reserva.PublicId.ToString() };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendBudgetMessageAsync(request, AdminActor(), CancellationToken.None));
        Assert.Contains("presupuesto", ex.Message);

        gateway.Verify(g => g.SendDocumentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await context.MessageDeliveries.AnyAsync());
    }

    [Fact]
    public async Task HappyPath_SendsPdfToCustomerAndRecordsDelivery()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        var reserva = BuildReserva(1, payer);
        context.Customers.Add(payer);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var (service, gateway, reservaService) = BuildService(context);
        reservaService
            .Setup(s => s.GetBudgetPdfAsync(reserva.PublicId.ToString(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new byte[] { 1, 2, 3 }, reserva.NumeroReserva));

        var request = new SendBudgetMessageRequest { ReservaId = reserva.PublicId.ToString(), PorPersona = true };

        var delivery = await service.SendBudgetMessageAsync(request, AdminActor(), CancellationToken.None);

        // Se uso el generador de PDF existente, con el mismo publicId y el porPersona pedido.
        reservaService.Verify(
            s => s.GetBudgetPdfAsync(reserva.PublicId.ToString(), true, It.IsAny<CancellationToken>()), Times.Once);

        // Se mando el documento por WhatsApp al telefono del cliente.
        gateway.Verify(g => g.SendDocumentAsync(
            "+5491155551234", It.IsAny<string>(), $"Presupuesto {reserva.NumeroReserva}.pdf",
            "application/pdf", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);

        // Se registro la entrega como Kind = "Budget", contra el cliente/reserva correctos.
        var persisted = await context.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryKinds.Budget, persisted.Kind);
        Assert.Equal(reserva.Id, persisted.ReservaId);
        Assert.Equal(payer.Id, persisted.CustomerId);
        Assert.Equal(MessageDeliveryStatuses.Sent, persisted.Status);
        Assert.Equal($"Presupuesto {reserva.NumeroReserva}.pdf", persisted.AttachmentName);

        Assert.Equal(MessageDeliveryKinds.Budget, delivery.Kind);
    }
}
