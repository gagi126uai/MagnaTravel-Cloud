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
/// Tanda 3 (2026-08-18): el timeline de la reserva ahora arma el evento de cambio de estado desde
/// <c>ReservaStatusChangeLogs</c> (mas rico: trae motivo y quien autorizo) en vez del diff generico de
/// <c>AuditLogs</c> (que solo tenia "de X a Y", sin motivo). Estos tests cubren: el evento nuevo aparece
/// con su motivo, el evento VIEJO (duplicado, generico) ya NO aparece, y el orden por fecha es correcto
/// aunque las dos fuentes se mezclen.
/// </summary>
public class TimelineServiceStatusChangeTests
{
    private static DbContextOptions<AppDbContext> BuildInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    [Fact]
    public async Task GetTimelineAsync_StatusChangeWithReason_AppearsWithReasonAndNoGenericDuplicate()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-TML-0001", Name = "Reserva timeline",
            Status = EstadoReserva.InManagement, TotalSale = 1000m, Balance = 0m
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var occurredAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
        context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = reserva.Id,
            FromStatus = EstadoReserva.Budget,
            ToStatus = EstadoReserva.InManagement,
            Direction = "Forward",
            ByUserId = "user-1",
            ByUserName = "Maite Vendedora",
            Reason = "El cliente confirmo el viaje por telefono.",
            OccurredAt = occurredAt,
        });

        // El evento VIEJO que este cambio de tanda reemplaza: el diff generico de AuditLogs con SOLO el
        // campo Status. Antes de este cambio generaba su propio evento duplicado ("Cambio en la
        // Reserva"); ahora IgnoredFields lo filtra y, al quedar sin campos utiles, el AuditLog entero
        // se descarta (no genera ningun TimelineEventDto).
        context.AuditLogs.Add(new AuditLog
        {
            UserId = "user-1",
            UserName = "Maite Vendedora",
            Action = "Update",
            EntityName = "Reserva",
            EntityId = reserva.Id.ToString(),
            Timestamp = occurredAt,
            Changes = "{\"Status\":{\"Old\":\"Budget\",\"New\":\"InManagement\"}}",
        });

        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        var statusEvent = Assert.Single(events, e => e.EventType == "StatusChange");
        Assert.Equal("Budget", statusEvent.FromStatus);
        Assert.Equal("InManagement", statusEvent.ToStatus);
        Assert.Contains("El cliente confirmo el viaje por telefono.", statusEvent.Details);
        Assert.Equal("Maite Vendedora", statusEvent.Actor);
        Assert.Equal("Reserva", statusEvent.RelatedEntityType);

        // El evento generico viejo (el que armaba el diff de AuditLogs con el bullet "Estado: de...")
        // no debe existir mas: es EXACTAMENTE el mismo evento, ahora reemplazado por el de arriba.
        Assert.DoesNotContain(events, e => e.Details != null && e.Details.Contains("• Estado:"));
    }

    [Fact]
    public async Task GetTimelineAsync_AuthorizedReversion_ShowsWhoAuthorized()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-TML-0002", Name = "Reserva reversion",
            Status = EstadoReserva.Budget, TotalSale = 1000m, Balance = 0m
        };
        context.Reservas.Add(reserva);
        context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = reserva.Id,
            FromStatus = EstadoReserva.InManagement,
            ToStatus = EstadoReserva.Budget,
            Direction = "Revert",
            ByUserId = "user-2",
            ByUserName = "Vendedor Junior",
            AuthorizedBySuperiorUserId = "admin-1",
            AuthorizedBySuperiorUserName = "Gaston Admin",
            Reason = "Se cargo mal el destino, hay que rehacer el presupuesto.",
            OccurredAt = new DateTime(2026, 8, 18, 11, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        var statusEvent = Assert.Single(events, e => e.EventType == "StatusChange");
        Assert.Contains("Autorizó: Gaston Admin", statusEvent.Details);
    }

    [Fact]
    public async Task GetTimelineAsync_MergesBothSourcesAndOrdersByTimestampDescending()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-TML-0003", Name = "Reserva orden",
            Status = EstadoReserva.Confirmed, TotalSale = 1000m, Balance = 0m
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var t1 = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2026, 8, 18, 11, 0, 0, DateTimeKind.Utc);

        // AuditLog mas viejo (t1) sobre un campo que SI se traduce (Notes en Reserva no esta mapeado a
        // texto legible por NormalizeFieldName, asi que usamos "Name" que si tiene traduccion).
        context.AuditLogs.Add(new AuditLog
        {
            UserId = "user-1", UserName = "Maite", Action = "Update", EntityName = "Reserva",
            EntityId = reserva.Id.ToString(), Timestamp = t1,
            Changes = "{\"Name\":{\"Old\":\"Viaje a Bariloche\",\"New\":\"Viaje a Bariloche (familia)\"}}",
        });

        // Cambio de estado en el medio (t2).
        context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = reserva.Id, FromStatus = EstadoReserva.InManagement, ToStatus = EstadoReserva.Confirmed,
            Direction = "Forward", ByUserId = "user-1", ByUserName = "Maite", OccurredAt = t2,
        });

        // AuditLog mas nuevo (t3).
        context.AuditLogs.Add(new AuditLog
        {
            UserId = "user-1", UserName = "Maite", Action = "Update", EntityName = "Reserva",
            EntityId = reserva.Id.ToString(), Timestamp = t3,
            Changes = "{\"Name\":{\"Old\":\"Viaje a Bariloche (familia)\",\"New\":\"Viaje a Bariloche (5 personas)\"}}",
        });

        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var allEvents = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        // El auto-audit generico de AppDbContext.OnBeforeSaveChanges tambien deja un evento "Create" al
        // dar de alta la Reserva (con Timestamp = ahora, ajeno a los tres timestamps fijos del test) —
        // se descarta aca porque no es parte de lo que este test verifica (el orden de Update/StatusChange).
        var events = allEvents.Where(e => e.EventType != "Create").ToList();

        Assert.Equal(3, events.Count);
        // De mas nuevo a mas viejo: t3, t2, t1.
        Assert.Equal(t3, events[0].Timestamp);
        Assert.Equal(t2, events[1].Timestamp);
        Assert.Equal(t1, events[2].Timestamp);
        Assert.Equal("StatusChange", events[1].EventType);
    }

    /// <summary>
    /// Bloqueante del reviewer (2026-08-18): el filtro de "Status" agregado en esta tanda es PUNTUAL a la
    /// Reserva (reemplazada por el evento mas rico de ReservaStatusChangeLogs), NO global. Otras entidades
    /// con su PROPIO campo Status (Payment, FlightSegment, HotelBooking, etc.) no tienen una fuente mas
    /// rica que las reemplace — sus cambios de Status tienen que seguir apareciendo en el timeline tal
    /// como aparecian antes de esta tanda (ej. "se cancelo un pago", "se confirmo un vuelo").
    /// </summary>
    [Fact]
    public async Task GetTimelineAsync_StatusFilterIsScopedToReserva_OtherEntitiesStillShowStatusChanges()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-TML-0004", Name = "Reserva con pago cancelado",
            Status = EstadoReserva.InManagement, TotalSale = 1000m, Balance = 0m
        };
        context.Reservas.Add(reserva);

        var payment = new Payment
        {
            ReservaId = reserva.Id, Amount = 500m, Currency = "ARS",
            Method = "Transfer", Status = "Cancelled",
        };
        context.Payments.Add(payment);
        await context.SaveChangesAsync();

        var paymentStatusChangedAt = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        context.AuditLogs.Add(new AuditLog
        {
            UserId = "user-1", UserName = "Maite", Action = "Update", EntityName = "Payment",
            EntityId = payment.PublicId.ToString(), Timestamp = paymentStatusChangedAt,
            // SOLO cambio Status (el mismo caso que, para Reserva, hoy se descarta entero).
            Changes = "{\"Status\":{\"Old\":\"Paid\",\"New\":\"Cancelled\"}}",
        });

        // Contraparte: la Reserva con SOLO Status como campo cambiado SIGUE descartandose (ese evento lo
        // reemplaza ReservaStatusChangeLogs, no el diff generico).
        context.AuditLogs.Add(new AuditLog
        {
            UserId = "user-1", UserName = "Maite", Action = "Update", EntityName = "Reserva",
            EntityId = reserva.Id.ToString(), Timestamp = paymentStatusChangedAt,
            Changes = "{\"Status\":{\"Old\":\"Budget\",\"New\":\"InManagement\"}}",
        });

        await context.SaveChangesAsync();

        var service = new TimelineService(context);
        var events = await service.GetTimelineAsync(reserva.Id, CancellationToken.None);

        var paymentEvent = Assert.Single(events, e => e.RelatedEntityType == "Payment" && e.EventType == "Update");
        Assert.Contains("Estado", paymentEvent.Details);

        Assert.DoesNotContain(events, e => e.RelatedEntityType == "Reserva" && e.EventType == "Update");
    }
}
