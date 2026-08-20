using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "la ficha del operador no borra la historia" (2026-08-20, punto 4): el timeline de la RESERVA
/// ahora suma eventos del circuito de cancelacion — multa del operador (confirmada / cerrada sin multa) y
/// notas de credito (emitida / rechazada por ARCA) — leidos DIRECTO de <c>BookingCancellationLine</c> /
/// <c>BookingCancellationCreditNote</c>, no del diff generico de AuditLog.
/// </summary>
public class TimelineServiceCancellationEventsTests
{
    private static DbContextOptions<AppDbContext> BuildInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static Reserva NewReserva(int id, string numero) => new()
    {
        Id = id, NumeroReserva = numero, Name = "Reserva " + numero,
        Status = EstadoReserva.Cancelled, TotalSale = 1000m, Balance = 0m,
    };

    // ===================== Multa del operador =====================

    [Fact]
    public async Task GetTimelineAsync_PenaltyConfirmed_ShowsAmountAndOperatorName()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = NewReserva(1, "F-PEN-1");
        var supplier = new Supplier { Id = 10, Name = "Aerolineas del Sur" };
        var bc = new BookingCancellation { Id = 100, ReservaId = reserva.Id, SupplierId = supplier.Id };
        var confirmedAt = new DateTime(2026, 8, 18, 18, 2, 0, DateTimeKind.Utc);
        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1, Scope = BookingCancellationLineScope.Full,
            Currency = "ARS", LineSaleAmount = 130000m,
            PenaltyStatus = PenaltyStatus.Confirmed, PenaltyAmount = 45000m, PenaltyCurrency = "ARS",
            PenaltyConfirmedAt = confirmedAt,
        };

        context.Reservas.Add(reserva);
        context.Suppliers.Add(supplier);
        context.BookingCancellations.Add(bc);
        context.BookingCancellationLines.Add(line);
        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        var penaltyEvent = Assert.Single(events, e => e.EventType == "OperatorPenaltyConfirmed");
        Assert.Equal(confirmedAt, penaltyEvent.Timestamp);
        Assert.Equal("La multa del operador quedó confirmada: $ 45.000.", penaltyEvent.Title);
        Assert.Equal(45000m, penaltyEvent.Amount);
        Assert.Equal("ARS", penaltyEvent.Currency);
        Assert.Contains("Aerolineas del Sur", penaltyEvent.Details);
    }

    /// <summary>
    /// Decision de producto FIJADA (review seguridad 2026-08-20, evidencia en el XML-doc de
    /// <c>TimelineService.BuildOperatorPenaltyEventsAsync</c>): el timeline de la RESERVA NO enmascara el
    /// monto de la multa del operador con <c>cobranzas.see_cost</c> — es la MISMA plata que
    /// <c>CancellationsController.GetByReserva</c> ya expone SIN mascara al mismo publico
    /// (<c>reservas.view</c> + ownership), via <c>FiscalLiquidation.OperatorPenaltyAmount</c> (F-12: la
    /// multa se traslada 1:1 al cliente). Este test es el candado: si un reviewer futuro agrega un gate
    /// aca, este test lo va a hacer fallar y va a tener que leer el porque antes de "arreglarlo".
    /// </summary>
    [Fact]
    public async Task GetTimelineAsync_PenaltyConfirmed_NeverMasksAmount_SameAudienceAsGetByReserva()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = NewReserva(1, "F-NOMASK-1");
        var supplier = new Supplier { Id = 10, Name = "Operador" };
        var bc = new BookingCancellation { Id = 100, ReservaId = reserva.Id, SupplierId = supplier.Id };
        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1, Scope = BookingCancellationLineScope.Full,
            Currency = "USD", LineSaleAmount = 5000m,
            PenaltyStatus = PenaltyStatus.Confirmed, PenaltyAmount = 800m, PenaltyCurrency = "USD",
            PenaltyConfirmedAt = DateTime.UtcNow,
        };
        context.Reservas.Add(reserva);
        context.Suppliers.Add(supplier);
        context.BookingCancellations.Add(bc);
        context.BookingCancellationLines.Add(line);
        await context.SaveChangesAsync();

        // TimelineService no recibe (ni pide) ningun contexto de permisos: la mascara F-14 de
        // "cobranzas.see_cost" NUNCA se evalua aca, a diferencia de SupplierService.GetSupplierTimelineAsync
        // (que SI la evalua). El gate real vive en el controller (ReservasController), fuera del alcance de
        // este test — lo que este test fija es que EL SERVICIO no introduce un enmascarado propio.
        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        var penaltyEvent = Assert.Single(events, e => e.EventType == "OperatorPenaltyConfirmed");
        Assert.Equal(800m, penaltyEvent.Amount);
        Assert.Equal("USD", penaltyEvent.Currency);
        // El Title CON monto (nunca la variante "sin numero" que usa SupplierService cuando enmascara).
        Assert.Equal("La multa del operador quedó confirmada: USD 800.", penaltyEvent.Title);
    }

    [Fact]
    public async Task GetTimelineAsync_PenaltyWaived_PrincipalOperator_ShowsActorFromBookingCancellation()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = NewReserva(1, "F-WAI-1");
        var supplier = new Supplier { Id = 10, Name = "Hotel Central" };
        var waivedAt = new DateTime(2026, 8, 18, 18, 2, 0, DateTimeKind.Utc);
        // Este proveedor ES el principal del BC (line.SupplierId == bc.SupplierId): el actor SI esta
        // disponible, persistido en el BC padre.
        var bc = new BookingCancellation
        {
            Id = 100, ReservaId = reserva.Id, SupplierId = supplier.Id,
            PenaltyConfirmedByUserName = "Gaston Admin",
        };
        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1, Scope = BookingCancellationLineScope.Full,
            Currency = "ARS", LineSaleAmount = 90000m,
            PenaltyStatus = PenaltyStatus.Waived, PenaltyConfirmedAt = waivedAt,
        };

        context.Reservas.Add(reserva);
        context.Suppliers.Add(supplier);
        context.BookingCancellations.Add(bc);
        context.BookingCancellationLines.Add(line);
        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        var waivedEvent = Assert.Single(events, e => e.EventType == "OperatorPenaltyWaived");
        Assert.Equal("Gaston Admin cerró la multa del operador sin cobrar nada.", waivedEvent.Title);
        Assert.Equal("Gaston Admin", waivedEvent.Actor);
    }

    [Fact]
    public async Task GetTimelineAsync_PenaltyWaived_SecondaryOperator_FallsBackToGenericTextWithoutActor()
    {
        // GAP conocido (ADR-044 T1): un operador SECUNDARIO no tiene el actor de "cerro sin multa"
        // persistido en ningun lado (solo el BC PADRE lo tiene, y ese dato es del operador PRINCIPAL).
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = NewReserva(1, "F-WAI-2");
        var principalSupplier = new Supplier { Id = 10, Name = "Aerolineas del Sur" };
        var secondarySupplier = new Supplier { Id = 11, Name = "Hotel Central" };
        var bc = new BookingCancellation
        {
            Id = 100, ReservaId = reserva.Id, SupplierId = principalSupplier.Id,
            PenaltyConfirmedByUserName = "Gaston Admin", // es del PRINCIPAL, no de este operador secundario.
        };
        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = secondarySupplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 2, Scope = BookingCancellationLineScope.Full,
            Currency = "ARS", LineSaleAmount = 60000m,
            PenaltyStatus = PenaltyStatus.Waived, PenaltyConfirmedAt = new DateTime(2026, 8, 18, 19, 0, 0, DateTimeKind.Utc),
        };

        context.Reservas.Add(reserva);
        context.Suppliers.AddRange(principalSupplier, secondarySupplier);
        context.BookingCancellations.Add(bc);
        context.BookingCancellationLines.Add(line);
        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        var waivedEvent = Assert.Single(events, e => e.EventType == "OperatorPenaltyWaived");
        Assert.Equal("Se cerró la multa del operador sin cobrar nada.", waivedEvent.Title);
        Assert.Equal("Sistema", waivedEvent.Actor);
    }

    [Fact]
    public async Task GetTimelineAsync_PenaltyEstimated_DoesNotAppearInTimeline()
    {
        // Estimated (todavia sin decidir) no es un evento "que paso": no corresponde en el timeline.
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = NewReserva(1, "F-EST-1");
        var supplier = new Supplier { Id = 10, Name = "Operador Cualquiera" };
        var bc = new BookingCancellation { Id = 100, ReservaId = reserva.Id, SupplierId = supplier.Id };
        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel, ServiceId = 1, Scope = BookingCancellationLineScope.Full,
            Currency = "ARS", LineSaleAmount = 10000m,
            PenaltyStatus = PenaltyStatus.Estimated,
        };

        context.Reservas.Add(reserva);
        context.Suppliers.Add(supplier);
        context.BookingCancellations.Add(bc);
        context.BookingCancellationLines.Add(line);
        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        Assert.DoesNotContain(events, e => e.EventType is "OperatorPenaltyConfirmed" or "OperatorPenaltyWaived");
    }

    // ===================== Notas de credito =====================

    [Fact]
    public async Task GetTimelineAsync_CreditNoteSucceeded_ShowsExactTextWithComprobanteLabel()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = NewReserva(1, "F-NC-1");
        var supplier = new Supplier { Id = 10, Name = "Operador" };
        var bc = new BookingCancellation { Id = 100, ReservaId = reserva.Id, SupplierId = supplier.Id };
        var invoice = new Invoice { Id = 500, TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 45 };
        var createdAt = new DateTime(2026, 8, 18, 17, 30, 0, DateTimeKind.Utc);
        var note = new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id, OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationCreditNoteStatus.Succeeded, CreatedAt = createdAt,
        };

        context.Reservas.Add(reserva);
        context.Suppliers.Add(supplier);
        context.Invoices.Add(invoice);
        context.BookingCancellations.Add(bc);
        context.BookingCancellationCreditNotes.Add(note);
        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        var ncEvent = Assert.Single(events, e => e.EventType == "CreditNoteEmitted");
        Assert.Equal(createdAt, ncEvent.Timestamp);
        Assert.EndsWith("— nota de crédito emitida.", ncEvent.Title);
    }

    [Fact]
    public async Task GetTimelineAsync_CreditNoteFailed_WithArcaMotivo_ShowsMotivoVerbatim()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = NewReserva(1, "F-NC-2");
        var supplier = new Supplier { Id = 10, Name = "Operador" };
        var bc = new BookingCancellation { Id = 100, ReservaId = reserva.Id, SupplierId = supplier.Id };
        var invoice = new Invoice { Id = 500, TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 45 };
        var note = new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id, OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationCreditNoteStatus.Failed,
            ArcaErrorMessage = "CAE rechazado: comprobante duplicado.",
            CreatedAt = new DateTime(2026, 8, 18, 17, 30, 0, DateTimeKind.Utc),
        };

        context.Reservas.Add(reserva);
        context.Suppliers.Add(supplier);
        context.Invoices.Add(invoice);
        context.BookingCancellations.Add(bc);
        context.BookingCancellationCreditNotes.Add(note);
        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        var ncEvent = Assert.Single(events, e => e.EventType == "CreditNoteRejected");
        Assert.Contains("la nota no salió. ARCA respondió: «CAE rechazado: comprobante duplicado.»", ncEvent.Title);
    }

    [Fact]
    public async Task GetTimelineAsync_CreditNotePending_DoesNotAppearInTimeline()
    {
        // Pending = todavia esperando el CAE: no es un evento resuelto, no corresponde mostrarlo (ver el
        // GAP documentado en TimelineService.BuildCreditNoteEventsAsync).
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = NewReserva(1, "F-NC-3");
        var supplier = new Supplier { Id = 10, Name = "Operador" };
        var bc = new BookingCancellation { Id = 100, ReservaId = reserva.Id, SupplierId = supplier.Id };
        var invoice = new Invoice { Id = 500, TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 45 };
        var note = new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id, OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationCreditNoteStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        context.Reservas.Add(reserva);
        context.Suppliers.Add(supplier);
        context.Invoices.Add(invoice);
        context.BookingCancellations.Add(bc);
        context.BookingCancellationCreditNotes.Add(note);
        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        Assert.DoesNotContain(events, e => e.EventType is "CreditNoteEmitted" or "CreditNoteRejected");
    }
}
