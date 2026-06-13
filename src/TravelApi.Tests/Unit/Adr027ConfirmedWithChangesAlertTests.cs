using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-027 (auditoria ERP, hallazgo #10): bucket de /alerts "reservas confirmadas con cambios sin revisar".
/// Aparece para reservas VIVAS marcadas (HasUnacknowledgedChanges=true) y NO para acuseadas o no-vivas.
/// Mismo gating de visibilidad que los otros buckets: admin todas, el vendedor solo SUS reservas.
/// </summary>
public class Adr027ConfirmedWithChangesAlertTests
{
    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(System.Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static AlertService BuildService(AppDbContext context)
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { UpcomingUnpaidReservationAlertDays = 30 });
        return new AlertService(context, mock.Object, NullLogger<AlertService>.Instance);
    }

    private static readonly AlertCallerContext Admin = new("admin", IsAdmin: true);

    private static Reserva BuildReserva(
        int id, string status, bool marked, string? responsible = "vendedor-A")
        => new()
        {
            Id = id,
            NumeroReserva = $"R-{id}",
            Name = $"Reserva {id}",
            Status = status,
            ResponsibleUserId = responsible,
            HasUnacknowledgedChanges = marked,
            ChangesPendingSince = marked ? System.DateTime.UtcNow : null
        };

    private static List<object> Bucket(object payload)
    {
        var value = payload.GetType().GetProperty("ConfirmedWithChanges")?.GetValue(payload);
        return value is System.Collections.IEnumerable items
            ? items.Cast<object>().ToList()
            : new List<object>();
    }

    [Fact]
    public async Task LiveMarkedReserva_AppearsInBucket()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, EstadoReserva.Confirmed, marked: true));
        await context.SaveChangesAsync();
        var service = BuildService(context);

        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var bucket = Bucket(payload);
        Assert.Single(bucket);
    }

    [Fact]
    public async Task AcknowledgedReserva_DoesNotAppear()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, EstadoReserva.Confirmed, marked: false));
        await context.SaveChangesAsync();
        var service = BuildService(context);

        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload));
    }

    [Theory]
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Lost)]
    public async Task MarkedButNonLiveReserva_DoesNotAppear(string nonLiveStatus)
    {
        // El flag puede quedar en true si la reserva cae a un estado no-vivo (se limpia recien al acusar);
        // pero el aviso no tiene sentido fuera de un estado vivo, asi que el bucket la filtra.
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, nonLiveStatus, marked: true));
        await context.SaveChangesAsync();
        var service = BuildService(context);

        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload));
    }

    [Fact]
    public async Task Seller_OnlySeesOwnMarkedReservas()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, EstadoReserva.Confirmed, marked: true, responsible: "vendedor-A"));
        context.Reservas.Add(BuildReserva(2, EstadoReserva.Confirmed, marked: true, responsible: "vendedor-B"));
        await context.SaveChangesAsync();
        var service = BuildService(context);

        var payload = await service.GetAlertsAsync(
            new AlertCallerContext("vendedor-A", IsAdmin: false), CancellationToken.None);

        var bucket = Bucket(payload);
        Assert.Single(bucket);
    }

    [Fact]
    public async Task NonAdminWithoutIdentity_SeesNothing()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildReserva(1, EstadoReserva.Confirmed, marked: true, responsible: null));
        await context.SaveChangesAsync();
        var service = BuildService(context);

        // Fail-closed: token sin identidad no ve avisos de nadie.
        var payload = await service.GetAlertsAsync(
            new AlertCallerContext(UserId: null, IsAdmin: false), CancellationToken.None);

        Assert.Empty(Bucket(payload));
    }
}
