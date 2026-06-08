using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-020: motor de estados automatico (confirmacion/regresion) + matriz de transiciones
/// (Lost, Cancelled manual, revert de Lost). Cubre los casos exigidos en §7.
/// </summary>
public class Adr020LifecycleTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ReservaAutoStateService NewEngine(AppDbContext context) =>
        new(context, NullLogger<ReservaAutoStateService>.Instance);

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService NewReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        // El mapper solo se usa en GetReservaByIdAsync (que RevertStatusAsync invoca al final).
        // Devolvemos un ReservaDto minimo que refleja el estado para poder asertar sobre el.
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
              .Returns((Reserva r) => new ReservaDto
              {
                  PublicId = r.PublicId, NumeroReserva = r.NumeroReserva, Name = r.Name, Status = r.Status
              });
        return new ReservaService(context, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static Reserva Reserva(int id, string status, string? responsibleUserId = null) => new()
    {
        Id = id, NumeroReserva = $"F-{id}", Name = $"Reserva {id}", Status = status,
        ResponsibleUserId = responsibleUserId
    };

    // ===================== Motor: confirmacion automatica =====================

    [Fact]
    public async Task Engine_AllResolved_PromotesInManagementToConfirmed_AndStampsConfirmedAt()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement));
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado" });
        await ctx.SaveChangesAsync();

        var changed = await NewEngine(ctx).EvaluateAndApplyAsync(1);

        Assert.True(changed);
        Assert.Equal(EstadoReserva.Confirmed, (await ctx.Reservas.FindAsync(1))!.Status);
        Assert.NotNull((await ctx.HotelBookings.FindAsync(10))!.ConfirmedAt);
        Assert.Single(ctx.ReservaStatusChangeLogs.Where(l => l.ToStatus == EstadoReserva.Confirmed));
    }

    [Fact]
    public async Task Engine_FlightHKWithoutTicket_DoesNotConfirm_UntilTicketIssued()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement));
        ctx.FlightSegments.Add(new FlightSegment { Id = 20, ReservaId = 1, Status = "HK", TicketIssuedAt = null });
        await ctx.SaveChangesAsync();

        // PNR confirmado pero sin ticket: NO resuelve -> sigue En gestion (pero estampa ConfirmedAt).
        var changed = await NewEngine(ctx).EvaluateAndApplyAsync(1);
        Assert.True(changed); // estampo ConfirmedAt
        Assert.Equal(EstadoReserva.InManagement, (await ctx.Reservas.FindAsync(1))!.Status);
        Assert.NotNull((await ctx.FlightSegments.FindAsync(20))!.ConfirmedAt);

        // Al emitir el ticket, resuelve -> Confirmada.
        (await ctx.FlightSegments.FindAsync(20))!.TicketIssuedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
        await NewEngine(ctx).EvaluateAndApplyAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    [Fact]
    public async Task Engine_Idempotent_DoesNotTransitionTwice()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement));
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado" });
        await ctx.SaveChangesAsync();

        var engine = NewEngine(ctx);
        await engine.EvaluateAndApplyAsync(1);
        var secondRun = await engine.EvaluateAndApplyAsync(1);

        Assert.False(secondRun); // ya estaba Confirmed y todo resuelto -> no-op
        Assert.Single(ctx.ReservaStatusChangeLogs.Where(l => l.ToStatus == EstadoReserva.Confirmed));
    }

    // ===================== Motor: regresion + notificacion =====================

    [Fact]
    public async Task Engine_ServiceBecomesUnresolved_RegressesConfirmedToInManagement_AndNotifiesResponsible()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow });
        // Servicio nuevo solicitado: rompe "todo resuelto".
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        var changed = await NewEngine(ctx).EvaluateAndApplyAsync(1);

        Assert.True(changed);
        Assert.Equal(EstadoReserva.InManagement, (await ctx.Reservas.FindAsync(1))!.Status);
        var notif = Assert.Single(ctx.Notifications.Where(n => n.RelatedEntityId == 1));
        Assert.Equal("vendedor-1", notif.UserId);
        Assert.Equal("Urgent", notif.Priority);
    }

    [Fact]
    public async Task Engine_Regression_SameDay_DoesNotDuplicateNotification()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        var engine = NewEngine(ctx);
        await engine.EvaluateAndApplyAsync(1); // regresa + notifica

        // Vuelve a Confirmada y vuelve a romperse el mismo dia.
        (await ctx.Reservas.FindAsync(1))!.Status = EstadoReserva.Confirmed;
        await ctx.SaveChangesAsync();
        await engine.EvaluateAndApplyAsync(1); // regresa de nuevo, NO debe duplicar

        Assert.Single(ctx.Notifications.Where(n => n.RelatedEntityId == 1));
    }

    [Fact]
    public async Task Engine_Regression_NoResponsible_FallsBackToAdmins()
    {
        await using var ctx = NewContext();
        // Sembrar un admin (rol + user + user-role).
        ctx.Users.Add(new ApplicationUser { Id = "admin-1", UserName = "admin", Email = "a@a.com", IsActive = true });
        ctx.Roles.Add(new IdentityRole { Id = "role-admin", Name = "Admin", NormalizedName = "ADMIN" });
        ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = "admin-1", RoleId = "role-admin" });
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: null));
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        await NewEngine(ctx).EvaluateAndApplyAsync(1);

        var notif = Assert.Single(ctx.Notifications.Where(n => n.RelatedEntityId == 1));
        Assert.Equal("admin-1", notif.UserId);
    }

    [Fact]
    public async Task Engine_SetsLastRegressionReason_OnRegression_AndClearsItOnReconfirm()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow });
        // Servicio nuevo solicitado: rompe "todo resuelto" -> regresion.
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        var engine = NewEngine(ctx);
        await engine.EvaluateAndApplyAsync(1);

        var afterRegression = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.InManagement, afterRegression!.Status);
        Assert.False(string.IsNullOrEmpty(afterRegression.LastRegressionReason));
        Assert.NotNull(afterRegression.LastRegressionAt);

        // Resolvemos el servicio pendiente -> el motor reconfirma y limpia la franja.
        (await ctx.HotelBookings.FindAsync(11))!.Status = "Confirmado";
        await ctx.SaveChangesAsync();
        await engine.EvaluateAndApplyAsync(1);

        var afterReconfirm = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, afterReconfirm!.Status);
        Assert.Null(afterReconfirm.LastRegressionReason);
        Assert.Null(afterReconfirm.LastRegressionAt);
    }

    [Fact]
    public async Task Engine_Reconciliation_SuppressesRegressionNotification_ButStillSetsFranja()
    {
        // La reconciliacion nocturna NO debe notificar (cura en lote), pero SI deja la franja naranja.
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        await NewEngine(ctx).EvaluateAndApplyAsync(1, suppressNotifications: true);

        Assert.Empty(ctx.Notifications.Where(n => n.RelatedEntityId == 1));
        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.InManagement, reserva!.Status);
        Assert.False(string.IsNullOrEmpty(reserva.LastRegressionReason));
    }

    // ===================== Matriz manual: Lost =====================

    [Fact]
    public async Task UpdateStatus_BudgetToLost_NoPayments_Allowed()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Budget));
        await ctx.SaveChangesAsync();

        var result = await NewReservaService(ctx).UpdateStatusAsync(1, EstadoReserva.Lost);

        Assert.Equal(EstadoReserva.Lost, result.Status);
    }

    [Fact]
    public async Task UpdateStatus_BudgetToLost_WithLivePayment_Rejected()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Budget));
        ctx.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 100m, Status = "Paid", IsDeleted = false });
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewReservaService(ctx).UpdateStatusAsync(1, EstadoReserva.Lost));
    }

    [Fact]
    public async Task UpdateStatus_QuotationToLost_WithLivePayment_Rejected()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Quotation));
        ctx.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 50m, Status = "Paid", IsDeleted = false });
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewReservaService(ctx).UpdateStatusAsync(1, EstadoReserva.Lost));
    }

    // ===================== Matriz manual: Cancelled (B5) =====================

    [Fact]
    public async Task UpdateStatus_CancelFromInManagement_Allowed()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement));
        await ctx.SaveChangesAsync();

        var result = await NewReservaService(ctx).UpdateStatusAsync(1, EstadoReserva.Cancelled);

        Assert.Equal(EstadoReserva.Cancelled, result.Status);
    }

    [Fact]
    public async Task UpdateStatus_CancelFromQuotation_Rejected()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Quotation));
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewReservaService(ctx).UpdateStatusAsync(1, EstadoReserva.Cancelled));
    }

    [Fact]
    public async Task UpdateStatus_ConfirmedTargetManual_Rejected()
    {
        // INV-020-02: Confirmed solo lo alcanza el motor, nunca UpdateStatusAsync manual.
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement));
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<System.ArgumentException>(
            () => NewReservaService(ctx).UpdateStatusAsync(1, EstadoReserva.Confirmed));
    }

    // ===================== Revert de Lost al estado de origen (B1) =====================

    [Fact]
    public async Task RevertLost_ReturnsToOriginStatusFromLog()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Lost));
        ctx.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = 1, FromStatus = EstadoReserva.Budget, ToStatus = EstadoReserva.Lost,
            Direction = "Forward", OccurredAt = DateTime.UtcNow.AddMinutes(-5)
        });
        await ctx.SaveChangesAsync();

        var result = await NewReservaService(ctx).RevertStatusAsync(
            "1",
            new RevertStatusRequest(EstadoReserva.Budget, null, "el cliente volvio a interesarse"),
            actorUserId: "admin-1", actorUserName: "Admin", actorIsAdmin: true, CancellationToken.None);

        Assert.Equal(EstadoReserva.Budget, result.Status);
    }
}
