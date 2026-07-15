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
/// Paso 5 (2026-06-24): envio de la FACTURA emitida al cliente por WhatsApp
/// (MessageService.SendInvoiceMessageAsync). Cubre los casos de negocio/seguridad: factura inexistente,
/// factura de OTRA reserva, factura NO emitida (sin CAE), cliente sin contacto, y el happy path que
/// registra la entrega como MessageDelivery (Kind = "Invoice") reusando la generacion de PDF existente.
/// </summary>
public class MessageServiceSendInvoiceTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public MessageServiceSendInvoiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    // Actor Admin: saltea los gates de permiso/ownership para que los tests se concentren en las reglas
    // especificas del envio de factura (existencia, vinculo con la reserva, estado fiscal, contacto).
    private static OperationActor AdminActor() =>
        new("admin-1", "Admin Test", new[] { "Admin" });

    private static Customer BuildCustomerWithPhone(int id, string phone) =>
        new() { Id = id, PublicId = Guid.NewGuid(), FullName = "Cliente Test", Phone = phone };

    private static Reserva BuildReserva(int id, Customer payer) =>
        new()
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            NumeroReserva = $"R-{id:000}",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            PayerId = payer.Id,
            Payer = payer
        };

    private static Invoice BuildIssuedInvoice(int id, int reservaId) =>
        new()
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            ReservaId = reservaId,
            TipoComprobante = 6, // Factura B
            PuntoDeVenta = 4,
            NumeroComprobante = 99,
            Resultado = "A",
            CAE = "74123456789012",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            ImporteTotal = 1000m,
            CreatedAt = DateTime.UtcNow
        };

    private static (MessageService Service, Mock<IWhatsAppGateway> Gateway, Mock<IInvoiceService> InvoiceService)
        BuildService(AppDbContext context)
    {
        var gateway = new Mock<IWhatsAppGateway>();
        gateway.Setup(g => g.SendDocumentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WhatsAppSendResult(true, "wamid-123", null));

        var invoiceService = new Mock<IInvoiceService>();
        invoiceService.Setup(s => s.GetPdfAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3 });

        var service = new MessageService(
            context,
            gateway.Object,
            new Mock<IVoucherService>().Object,
            invoiceService.Object);

        return (service, gateway, invoiceService);
    }

    private static SendInvoiceMessageRequest BuildRequest(Reserva reserva, Customer payer, Invoice invoice) =>
        new()
        {
            PersonType = "customer",
            PersonId = payer.PublicId.ToString(),
            ReservaId = reserva.PublicId.ToString(),
            InvoicePublicId = invoice.PublicId.ToString()
        };

    [Fact]
    public async Task NonExistentInvoice_Throws()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        var reserva = BuildReserva(1, payer);
        context.Customers.Add(payer);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var (service, _, _) = BuildService(context);
        var request = new SendInvoiceMessageRequest
        {
            PersonType = "customer",
            PersonId = payer.PublicId.ToString(),
            ReservaId = reserva.PublicId.ToString(),
            InvoicePublicId = Guid.NewGuid().ToString() // no existe
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.SendInvoiceMessageAsync(request, AdminActor(), CancellationToken.None));
    }

    [Fact]
    public async Task InvoiceFromAnotherReserva_Throws()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        var reservaA = BuildReserva(1, payer);
        var reservaB = BuildReserva(2, payer);
        var invoiceOfB = BuildIssuedInvoice(50, reservaB.Id);
        context.Customers.Add(payer);
        context.Reservas.AddRange(reservaA, reservaB);
        context.Invoices.Add(invoiceOfB);
        await context.SaveChangesAsync();

        var (service, _, _) = BuildService(context);
        // Pido enviar la factura de la reserva B pero apuntando a la reserva A.
        var request = new SendInvoiceMessageRequest
        {
            PersonType = "customer",
            PersonId = payer.PublicId.ToString(),
            ReservaId = reservaA.PublicId.ToString(),
            InvoicePublicId = invoiceOfB.PublicId.ToString()
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendInvoiceMessageAsync(request, AdminActor(), CancellationToken.None));
        Assert.Contains("no corresponde a la reserva", ex.Message);
    }

    [Fact]
    public async Task NotIssuedInvoice_Throws()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        var reserva = BuildReserva(1, payer);
        var pendingInvoice = BuildIssuedInvoice(60, reserva.Id);
        pendingInvoice.Resultado = "PENDING";
        pendingInvoice.CAE = null; // todavia sin CAE
        context.Customers.Add(payer);
        context.Reservas.Add(reserva);
        context.Invoices.Add(pendingInvoice);
        await context.SaveChangesAsync();

        var (service, _, _) = BuildService(context);
        var request = BuildRequest(reserva, payer, pendingInvoice);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendInvoiceMessageAsync(request, AdminActor(), CancellationToken.None));
        Assert.Contains("todavia no esta emitida", ex.Message);
    }

    [Fact]
    public async Task CustomerWithoutContact_Throws()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, null!); // sin telefono
        var reserva = BuildReserva(1, payer);
        var invoice = BuildIssuedInvoice(70, reserva.Id);
        context.Customers.Add(payer);
        context.Reservas.Add(reserva);
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();

        var (service, _, _) = BuildService(context);
        var request = BuildRequest(reserva, payer, invoice);

        // ResolveRecipientAsync tira InvalidOperationException ("no tiene telefono asociado") cuando el
        // cliente no tiene contacto cargado.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendInvoiceMessageAsync(request, AdminActor(), CancellationToken.None));
    }

    [Fact]
    public async Task CreditNote_Throws()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        var reserva = BuildReserva(1, payer);
        // NC aprobada por ARCA: vive en la misma tabla, queda con Resultado="A" + CAE, pero NO es una
        // factura de venta. Tipo 3 = Nota de Credito A.
        var creditNote = BuildIssuedInvoice(90, reserva.Id);
        creditNote.TipoComprobante = 3;
        context.Customers.Add(payer);
        context.Reservas.Add(reserva);
        context.Invoices.Add(creditNote);
        await context.SaveChangesAsync();

        var (service, gateway, _) = BuildService(context);
        var request = BuildRequest(reserva, payer, creditNote);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendInvoiceMessageAsync(request, AdminActor(), CancellationToken.None));
        Assert.Contains("nota de credito", ex.Message);

        // No se envio nada ni se registro entrega.
        gateway.Verify(g => g.SendDocumentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await context.MessageDeliveries.AnyAsync());
    }

    [Fact]
    public async Task AnnulledInvoice_Throws()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        var reserva = BuildReserva(1, payer);
        // Factura de venta valida (con CAE) pero YA ANULADA (NC total aprobada): no se reenvia.
        var annulled = BuildIssuedInvoice(91, reserva.Id);
        annulled.AnnulmentStatus = AnnulmentStatus.Succeeded;
        context.Customers.Add(payer);
        context.Reservas.Add(reserva);
        context.Invoices.Add(annulled);
        await context.SaveChangesAsync();

        var (service, gateway, _) = BuildService(context);
        var request = BuildRequest(reserva, payer, annulled);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendInvoiceMessageAsync(request, AdminActor(), CancellationToken.None));
        Assert.Contains("anulada", ex.Message);

        gateway.Verify(g => g.SendDocumentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(await context.MessageDeliveries.AnyAsync());
    }

    [Fact]
    public async Task HappyPath_SendsPdfAndRecordsDelivery()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        var reserva = BuildReserva(1, payer);
        var invoice = BuildIssuedInvoice(80, reserva.Id);
        context.Customers.Add(payer);
        context.Reservas.Add(reserva);
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();

        var (service, gateway, invoiceService) = BuildService(context);
        var request = BuildRequest(reserva, payer, invoice);

        var delivery = await service.SendInvoiceMessageAsync(request, AdminActor(), CancellationToken.None);

        // Se uso la generacion de PDF existente.
        invoiceService.Verify(s => s.GetPdfAsync(invoice.Id, It.IsAny<CancellationToken>()), Times.Once);
        // Se mando el documento por WhatsApp.
        gateway.Verify(g => g.SendDocumentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            "application/pdf", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);

        // Se registro la entrega como Kind = "Invoice".
        var persisted = await context.MessageDeliveries.SingleAsync();
        Assert.Equal(MessageDeliveryKinds.Invoice, persisted.Kind);
        Assert.Equal(reserva.Id, persisted.ReservaId);
        Assert.Equal(payer.Id, persisted.CustomerId);
        Assert.Equal(MessageDeliveryStatuses.Sent, persisted.Status);
        Assert.NotNull(persisted.AttachmentName);

        Assert.Equal(MessageDeliveryKinds.Invoice, delivery.Kind);
    }

    [Fact]
    public async Task PartialCreditNote_HappyPath_DerivesDocumentAndCustomerFromCancellation()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        var reserva = BuildReserva(1, payer);
        var supplier = new Supplier { Id = 1, Name = "Operador", IsActive = true };
        var sale = BuildIssuedInvoice(100, reserva.Id);
        var creditNote = BuildIssuedInvoice(101, reserva.Id);
        creditNote.TipoComprobante = 8;
        context.AddRange(payer, supplier, reserva, sale, creditNote);
        await context.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = payer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = sale.Id,
            Status = BookingCancellationStatus.Closed,
            Reason = "Cancelación parcial de prueba",
        };
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            Scope = BookingCancellationLineScope.Partial,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Currency = "ARS",
            LineSaleAmount = 1000m,
        });
        bc.CreditNotes.Add(new BookingCancellationCreditNote
        {
            OriginatingInvoiceId = sale.Id,
            CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationCreditNoteStatus.Succeeded,
            ArcaCurrency = "PES",
        });
        context.BookingCancellations.Add(bc);
        await context.SaveChangesAsync();
        var (service, gateway, invoiceService) = BuildService(context);

        var delivery = await service.SendPartialCreditNoteMessageAsync(bc.PublicId, AdminActor(), CancellationToken.None);

        invoiceService.Verify(s => s.GetPdfAsync(creditNote.Id, It.IsAny<CancellationToken>()), Times.Once);
        gateway.Verify(g => g.SendDocumentAsync(
            It.IsAny<string>(), It.Is<string>(caption => caption.Contains("nota de crédito")),
            It.Is<string>(name => name.StartsWith("Nota-de-credito-")), "application/pdf",
            It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(MessageDeliveryKinds.CreditNote, delivery.Kind);
        Assert.Equal(MessageDeliveryKinds.CreditNote, (await context.MessageDeliveries.SingleAsync()).Kind);
    }

    [Fact]
    public async Task PartialCreditNote_RejectsFullCancellationEvenWithSucceededCreditNote()
    {
        using var context = new AppDbContext(_dbOptions);
        var payer = BuildCustomerWithPhone(1, "+5491155551234");
        var reserva = BuildReserva(1, payer);
        var supplier = new Supplier { Id = 1, Name = "Operador", IsActive = true };
        var sale = BuildIssuedInvoice(110, reserva.Id);
        var creditNote = BuildIssuedInvoice(111, reserva.Id);
        creditNote.TipoComprobante = 8;
        context.AddRange(payer, supplier, reserva, sale, creditNote);
        await context.SaveChangesAsync();
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = payer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = sale.Id, Status = BookingCancellationStatus.Closed, Reason = "Total",
        };
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id, Scope = BookingCancellationLineScope.Full,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1, Currency = "ARS",
        });
        bc.CreditNotes.Add(new BookingCancellationCreditNote
        {
            OriginatingInvoiceId = sale.Id, CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationCreditNoteStatus.Succeeded,
        });
        context.Add(bc);
        await context.SaveChangesAsync();
        var (service, gateway, _) = BuildService(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendPartialCreditNoteMessageAsync(bc.PublicId, AdminActor(), CancellationToken.None));
        gateway.Verify(g => g.SendDocumentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
