using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Notifications;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Decision firmada 2026-08-18 (Gaston): los errores de "Marcar confirmado/emitido/No requiere
/// confirmacion" sobre un servicio TAMBIEN quedan en la campanita (solo errores, nunca exitos). Estos
/// tests cubren <see cref="ServiceResolutionFailureNotifier"/> directo (sin pasar por el action filter ni
/// por HTTP), que es donde vive toda la logica de negocio del aviso.
/// </summary>
public class ServiceResolutionFailureNotifierTests
{
    // ============================================================
    // Armado (mismo patron que NotificationResolutionTests).
    // ============================================================

    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"svc-resolution-notifier-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed class NoopDispatcher : INotificationRealtimeDispatcher
    {
        public Task DispatchAsync(Notification notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static Mock<UserManager<ApplicationUser>> BuildUserManagerWithAdmins(params ApplicationUser[] admins)
    {
        var storeMock = new Mock<IUserStore<ApplicationUser>>();
        var userManagerMock = new Mock<UserManager<ApplicationUser>>(
            storeMock.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync((IList<ApplicationUser>)new List<ApplicationUser>(admins));
        return userManagerMock;
    }

    private static ServiceResolutionFailureNotifier BuildNotifier(AppDbContext ctx, params ApplicationUser[] admins)
    {
        var notificationService = new NotificationService(new Repository<Notification>(ctx), new NoopDispatcher());
        return new ServiceResolutionFailureNotifier(
            ctx,
            notificationService,
            BuildUserManagerWithAdmins(admins).Object,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<ServiceResolutionFailureNotifier>>());
    }

    /// <summary>Vuelo minimo + su reserva, para que ResolveServiceInfoAsync tenga algo que encontrar.</summary>
    private static (Reserva reserva, FlightSegment flight) SeedFlight(AppDbContext ctx, int reservaId = 1, int flightId = 501)
    {
        var reserva = new Reserva { Id = reservaId, NumeroReserva = "2026-1067", Name = "Cliente de prueba" };
        var flight = new FlightSegment { Id = flightId, ReservaId = reservaId, PublicId = Guid.NewGuid() };
        ctx.Reservas.Add(reserva);
        ctx.FlightSegments.Add(flight);
        ctx.SaveChanges();
        return (reserva, flight);
    }

    // ============================================================
    // A) Rechazo de negocio -> aviso creado, con el numero HUMANO de la reserva (nunca un id ni un GUID).
    // ============================================================

    [Fact]
    public async Task NotifyFailure_CreatesLiveErrorNotification_WithHumanReservaNumber_NoGuidOrInternalId()
    {
        var ctx = NewDbContext();
        var (_, flight) = SeedFlight(ctx);
        var admin = new ApplicationUser { Id = "admin-1", UserName = "admin", Email = "a@a.com", IsActive = true };
        var notifier = BuildNotifier(ctx, admin);

        await notifier.NotifyFailureAsync(
            ServiceResolutionKind.FlightSegment,
            flight.PublicId.ToString(),
            "El operador todavía no confirmó la reserva.",
            CancellationToken.None);

        var created = await ctx.Notifications.AsNoTracking().SingleAsync();
        Assert.Equal("admin-1", created.UserId);
        Assert.Equal(NotificationTypes.Error, created.Type);
        Assert.Equal("Normal", created.Priority); // NO Urgent: no debe disparar el banner.
        Assert.Equal(NotificationRelatedEntityTypes.Reserva, created.RelatedEntityType);
        Assert.Equal(1, created.RelatedEntityId); // id interno de la RESERVA (no del servicio).
        Assert.Equal("ServicioResolucionError:FlightSegment:501", created.ResolutionKey);

        Assert.Equal(
            "No se pudo confirmar un servicio con el operador — Reserva 2026-1067: El operador todavía no confirmó la reserva.",
            created.Message);

        // Gate de exposicion de datos: nada de GUIDs ni el id interno 501/1 sueltos en el texto.
        Assert.DoesNotContain(flight.PublicId.ToString(), created.Message);
        Assert.DoesNotContain("501", created.Message);
    }

    [Fact]
    public async Task NotifyFailure_OneNotificationPerAdmin()
    {
        var ctx = NewDbContext();
        var (_, flight) = SeedFlight(ctx);
        var admin1 = new ApplicationUser { Id = "admin-1", UserName = "a1", Email = "a1@a.com", IsActive = true };
        var admin2 = new ApplicationUser { Id = "admin-2", UserName = "a2", Email = "a2@a.com", IsActive = true };
        var notifier = BuildNotifier(ctx, admin1, admin2);

        await notifier.NotifyFailureAsync(ServiceResolutionKind.FlightSegment, flight.PublicId.ToString(), "rechazo", CancellationToken.None);

        Assert.Equal(2, await ctx.Notifications.CountAsync());
    }

    // ============================================================
    // B) Dedup: un segundo fallo con aviso VIVO no duplica.
    // ============================================================

    [Fact]
    public async Task NotifyFailure_WithLiveNotificationAlready_DoesNotDuplicate()
    {
        var ctx = NewDbContext();
        var (_, flight) = SeedFlight(ctx);
        var admin = new ApplicationUser { Id = "admin-1", UserName = "admin", Email = "a@a.com", IsActive = true };
        var notifier = BuildNotifier(ctx, admin);

        await notifier.NotifyFailureAsync(ServiceResolutionKind.FlightSegment, flight.PublicId.ToString(), "primer rechazo", CancellationToken.None);
        await notifier.NotifyFailureAsync(ServiceResolutionKind.FlightSegment, flight.PublicId.ToString(), "segundo rechazo (mismo servicio)", CancellationToken.None);

        Assert.Equal(1, await ctx.Notifications.CountAsync());
        var stored = await ctx.Notifications.AsNoTracking().SingleAsync();
        // Mismo criterio que los jobs recurrentes (CashLedgerRefundReconciliationJob, etc.): se saltea,
        // no se pisa el mensaje del aviso ya creado.
        Assert.Contains("primer rechazo", stored.Message);
    }

    // ============================================================
    // C) Exito posterior -> el aviso vivo se apaga solo (ResolvedAt seteado). Ningun aviso nuevo.
    // ============================================================

    [Fact]
    public async Task NotifyResolved_AfterFailure_ResolvesTheLiveNotification_WithoutCreatingAnother()
    {
        var ctx = NewDbContext();
        var (_, flight) = SeedFlight(ctx);
        var admin = new ApplicationUser { Id = "admin-1", UserName = "admin", Email = "a@a.com", IsActive = true };
        var notifier = BuildNotifier(ctx, admin);

        await notifier.NotifyFailureAsync(ServiceResolutionKind.FlightSegment, flight.PublicId.ToString(), "rechazo", CancellationToken.None);
        await notifier.NotifyResolvedAsync(ServiceResolutionKind.FlightSegment, flight.PublicId.ToString(), CancellationToken.None);

        var stored = await ctx.Notifications.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.ResolvedAt);
        Assert.Equal(1, await ctx.Notifications.CountAsync()); // el exito NUNCA crea un aviso nuevo.
    }

    [Fact]
    public async Task NotifyResolved_WithoutAnyPriorFailure_IsNoOp()
    {
        var ctx = NewDbContext();
        var (_, flight) = SeedFlight(ctx);
        var admin = new ApplicationUser { Id = "admin-1", UserName = "admin", Email = "a@a.com", IsActive = true };
        var notifier = BuildNotifier(ctx, admin);

        await notifier.NotifyResolvedAsync(ServiceResolutionKind.FlightSegment, flight.PublicId.ToString(), CancellationToken.None);

        Assert.Equal(0, await ctx.Notifications.CountAsync());
    }

    // ============================================================
    // D) Blindaje: servicio inexistente (o id invalido) nunca tira excepcion y no crea nada.
    // ============================================================

    [Fact]
    public async Task NotifyFailure_ServiceNotFound_DoesNotThrow_AndCreatesNothing()
    {
        var ctx = NewDbContext(); // sin seed: el vuelo "no existe".
        var admin = new ApplicationUser { Id = "admin-1", UserName = "admin", Email = "a@a.com", IsActive = true };
        var notifier = BuildNotifier(ctx, admin);

        var exception = await Record.ExceptionAsync(() =>
            notifier.NotifyFailureAsync(ServiceResolutionKind.FlightSegment, Guid.NewGuid().ToString(), "rechazo", CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(0, await ctx.Notifications.CountAsync());
    }

    [Fact]
    public async Task NotifyFailure_NoAdminUsers_DoesNotThrow_AndCreatesNothing()
    {
        var ctx = NewDbContext();
        var (_, flight) = SeedFlight(ctx);
        var notifier = BuildNotifier(ctx); // sin admins.

        var exception = await Record.ExceptionAsync(() =>
            notifier.NotifyFailureAsync(ServiceResolutionKind.FlightSegment, flight.PublicId.ToString(), "rechazo", CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(0, await ctx.Notifications.CountAsync());
    }
}
