using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Authorization;
using TravelApi.Infrastructure.Persistence;
using Xunit;

namespace TravelApi.Tests.Unit.Authorization;

/// <summary>
/// B1.15 Fase 1 — OwnershipResolver.
///
/// Cubre:
///  - Reserva: owner exacto, owner distinto, ResponsibleUserId NULL (legacy), no existe.
///  - Servicio/Payment/Invoice/Voucher: lookup via Reserva padre.
///  - Identificacion por PublicId (Guid) y por legacy id (int).
///  - Inputs invalidos (string vacio, id no parseable).
/// </summary>
public class OwnershipResolverTests
{
    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Reserva> SeedReservaAsync(AppDbContext ctx, string? responsibleUserId, int id = 1)
    {
        var reserva = new Reserva
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            NumeroReserva = $"F-2026-{id:D4}",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = responsibleUserId,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return reserva;
    }

    [Fact]
    public async Task Reserva_owner_match_returns_true()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, reserva.PublicId.ToString());

        Assert.True(result);
    }

    [Fact]
    public async Task Reserva_owner_mismatch_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-2", OwnedEntity.Reserva, reserva.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Reserva_with_null_responsible_returns_false()
    {
        // Decision Gaston: legacy sin backfill no asume ownership.
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, responsibleUserId: null);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, reserva.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Reserva_not_found_returns_false()
    {
        await using var ctx = BuildContext();
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, Guid.NewGuid().ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Reserva_lookup_by_legacy_id_works()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1", id: 42);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, "42");

        Assert.True(result);
    }

    [Fact]
    public async Task Lead_owner_match_uses_assigned_user()
    {
        await using var ctx = BuildContext();
        var lead = new Lead { FullName = "Consulta", AssignedToUserId = "user-1" };
        ctx.Leads.Add(lead);
        await ctx.SaveChangesAsync();
        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Lead, lead.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Lead, lead.PublicId.ToString()));
    }

    [Fact]
    public async Task Lead_without_assignment_fails_closed()
    {
        await using var ctx = BuildContext();
        var lead = new Lead { FullName = "Consulta", AssignedToUserId = null };
        ctx.Leads.Add(lead);
        await ctx.SaveChangesAsync();
        var resolver = new OwnershipResolver(ctx);

        Assert.False(await resolver.IsOwnerAsync("user-1", OwnedEntity.Lead, lead.PublicId.ToString()));
    }

    [Fact]
    public async Task Quote_owner_is_inherited_from_assigned_lead()
    {
        await using var ctx = BuildContext();
        var quote = new Quote
        {
            QuoteNumber = "HIST-1",
            Title = "Histórica",
            Lead = new Lead { FullName = "Consulta", AssignedToUserId = "user-1" }
        };
        ctx.Quotes.Add(quote);
        await ctx.SaveChangesAsync();
        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Quote, quote.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Quote, quote.PublicId.ToString()));
    }

    [Fact]
    public async Task Servicio_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var servicio = new ServicioReserva
        {
            Id = 100,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
        };
        ctx.Servicios.Add(servicio);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Servicio, servicio.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-X", OwnedEntity.Servicio, servicio.PublicId.ToString()));
    }

    [Fact]
    public async Task Payment_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var payment = new Payment
        {
            Id = 200,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            Amount = 100m,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Payment, payment.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Payment, payment.PublicId.ToString()));
    }

    [Fact]
    public async Task Invoice_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var invoice = new Invoice
        {
            Id = 300,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            ImporteTotal = 1000m,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Invoice, invoice.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Invoice, invoice.PublicId.ToString()));
    }

    [Fact]
    public async Task Voucher_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var voucher = new Voucher
        {
            Id = 400,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
        };
        ctx.Vouchers.Add(voucher);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Voucher, voucher.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Voucher, voucher.PublicId.ToString()));
    }

    // 2026-06-03: cierre IDOR AttachmentsController. Attachment hereda el
    // ResponsibleUserId via su Reserva padre, igual que Voucher. Estos tests son
    // la garantia a nivel resolver de que un usuario que no es responsable (y sin
    // bypass) NO pasa el chequeo de ownership al descargar/renombrar/borrar adjuntos.

    [Fact]
    public async Task Attachment_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var attachment = new ReservaAttachment
        {
            Id = 600,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            FileName = "pasaporte.pdf",
            StoredFileName = "stored-pasaporte.pdf",
        };
        ctx.ReservaAttachments.Add(attachment);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Attachment, attachment.PublicId.ToString()));
        // Usuario que NO es responsable de la reserva: denegado (sin bypass).
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Attachment, attachment.PublicId.ToString()));
    }

    [Fact]
    public async Task Attachment_with_legacy_reserva_without_responsible_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, responsibleUserId: null);

        var attachment = new ReservaAttachment
        {
            Id = 601,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            FileName = "doc.pdf",
            StoredFileName = "stored-doc.pdf",
        };
        ctx.ReservaAttachments.Add(attachment);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Attachment, attachment.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Attachment_not_found_returns_false()
    {
        await using var ctx = BuildContext();
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Attachment, Guid.NewGuid().ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Attachment_lookup_by_legacy_id_works()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var attachment = new ReservaAttachment
        {
            Id = 602,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            FileName = "voucher.pdf",
            StoredFileName = "stored-voucher.pdf",
        };
        ctx.ReservaAttachments.Add(attachment);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Attachment, "602"));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Attachment, "602"));
    }

    [Fact]
    public async Task Passenger_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var passenger = new Passenger
        {
            Id = 500,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
        };
        ctx.Passengers.Add(passenger);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Passenger, passenger.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Passenger, passenger.PublicId.ToString()));
    }

    [Fact]
    public async Task Servicio_with_legacy_reserva_without_responsible_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, responsibleUserId: null);

        var servicio = new ServicioReserva
        {
            Id = 700,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
        };
        ctx.Servicios.Add(servicio);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Servicio, servicio.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Empty_userId_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(string.Empty, OwnedEntity.Reserva, reserva.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Unparseable_id_returns_false()
    {
        await using var ctx = BuildContext();
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, "no-es-guid-ni-int");

        Assert.False(result);
    }

    // ============================================================
    // FC1.2.0 v3 (2026-05-17): nuevos handlers OwnedEntity.BookingCancellation
    // y OwnedEntity.ClientCreditEntry. Reusan el patron Reserva: heredan el
    // ResponsibleUserId via la Reserva padre.
    // ============================================================

    /// <summary>
    /// Helper minimo para crear un BookingCancellation con todas las FK requeridas.
    /// Los CHECK constraints SQL (INV-118 fiscal snapshot) los aplica solo Postgres,
    /// no InMemory, asi que aca podemos construir un BC en Drafted sin FiscalSnapshot
    /// completo y testear el resolver puro.
    /// </summary>
    private static async Task<BookingCancellation> SeedBookingCancellationAsync(
        AppDbContext ctx,
        Reserva reserva,
        int bcId = 1000)
    {
        var customer = new Customer
        {
            Id = bcId + 1,
            PublicId = Guid.NewGuid(),
            FullName = "Cliente Test",
            TaxCondition = "Consumidor Final",
        };
        ctx.Customers.Add(customer);

        var supplier = new Supplier
        {
            Id = bcId + 2,
            PublicId = Guid.NewGuid(),
            Name = "Operador Test",
        };
        ctx.Suppliers.Add(supplier);

        var invoice = new Invoice
        {
            Id = bcId + 3,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            ImporteTotal = 1000m,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            Id = bcId,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Reason = "test",
            DraftedByUserId = "user-vendor",
            FiscalSnapshot = new FiscalSnapshot(),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();
        return bc;
    }

    [Fact]
    public async Task BookingCancellation_resolves_via_parent_reserva_owner_match()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var bc = await SeedBookingCancellationAsync(ctx, reserva);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(
            "user-1", OwnedEntity.BookingCancellation, bc.PublicId.ToString());

        Assert.True(result);
    }

    [Fact]
    public async Task BookingCancellation_owner_mismatch_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var bc = await SeedBookingCancellationAsync(ctx, reserva);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(
            "user-2", OwnedEntity.BookingCancellation, bc.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task BookingCancellation_with_legacy_reserva_without_responsible_returns_false()
    {
        // Decision Gaston (B1.15): reservas legacy sin ResponsibleUserId NO
        // asumen ownership para nadie. Vendedor con permiso ReservasView no
        // puede cancelar lo que no esta backfilled.
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, responsibleUserId: null);
        var bc = await SeedBookingCancellationAsync(ctx, reserva);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(
            "user-1", OwnedEntity.BookingCancellation, bc.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task BookingCancellation_not_found_returns_false()
    {
        await using var ctx = BuildContext();
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(
            "user-1", OwnedEntity.BookingCancellation, Guid.NewGuid().ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task BookingCancellation_lookup_by_legacy_id_works()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var bc = await SeedBookingCancellationAsync(ctx, reserva, bcId: 9999);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(
            "user-1", OwnedEntity.BookingCancellation, "9999");

        Assert.True(result);
    }

    private static async Task<ClientCreditEntry> SeedClientCreditEntryAsync(
        AppDbContext ctx,
        BookingCancellation bc,
        int entryId = 2000)
    {
        // OperatorRefund minimo para satisfacer la FK del Allocation.
        var refund = new OperatorRefundReceived
        {
            Id = entryId + 1,
            PublicId = Guid.NewGuid(),
            SupplierId = bc.SupplierId,
            ReceivedAmount = 5000m,
            Currency = "ARS",
            Method = "Transfer",
            ReceivedByUserId = "user-cashier",
            ReceivedByUserName = "Cashier",
        };
        ctx.OperatorRefundReceived.Add(refund);

        var allocation = new OperatorRefundAllocation
        {
            Id = entryId + 2,
            PublicId = Guid.NewGuid(),
            OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc.Id,
            GrossAmount = 1000m,
            NetAmount = 1000m,
            CreatedByUserId = "user",
        };
        ctx.OperatorRefundAllocations.Add(allocation);
        await ctx.SaveChangesAsync();

        var entry = new ClientCreditEntry
        {
            Id = entryId,
            PublicId = Guid.NewGuid(),
            CustomerId = bc.CustomerId,
            OperatorRefundAllocationId = allocation.Id,
            BookingCancellationId = bc.Id,
            CreditedAmount = 1000m,
            RemainingBalance = 1000m,
        };
        ctx.ClientCreditEntries.Add(entry);
        await ctx.SaveChangesAsync();
        return entry;
    }

    [Fact]
    public async Task ClientCreditEntry_resolves_via_bc_and_reserva_owner_match()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var bc = await SeedBookingCancellationAsync(ctx, reserva);
        var entry = await SeedClientCreditEntryAsync(ctx, bc);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(
            "user-1", OwnedEntity.ClientCreditEntry, entry.PublicId.ToString());

        Assert.True(result);
    }

    [Fact]
    public async Task ClientCreditEntry_owner_mismatch_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var bc = await SeedBookingCancellationAsync(ctx, reserva);
        var entry = await SeedClientCreditEntryAsync(ctx, bc);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(
            "user-9", OwnedEntity.ClientCreditEntry, entry.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task ClientCreditEntry_with_legacy_reserva_without_responsible_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, responsibleUserId: null);
        var bc = await SeedBookingCancellationAsync(ctx, reserva);
        var entry = await SeedClientCreditEntryAsync(ctx, bc);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(
            "user-1", OwnedEntity.ClientCreditEntry, entry.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task ClientCreditEntry_not_found_returns_false()
    {
        await using var ctx = BuildContext();
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(
            "user-1", OwnedEntity.ClientCreditEntry, Guid.NewGuid().ToString());

        Assert.False(result);
    }
}
