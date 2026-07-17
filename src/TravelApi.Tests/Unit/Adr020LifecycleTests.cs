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

    // ===================== Motor: "confirmada con cambios" (reemplaza la vieja regresion) =====================
    //
    // CAMBIO DE FONDO 2026-06-24 (alineado a Odoo/SAP): una reserva confirmada que deja de tener todos sus
    // servicios resueltos YA NO regresa sola a En gestion. Queda EN Confirmed pero MARCADA "confirmada con
    // cambios / revisar" (HasUnacknowledgedChanges) + aviso urgente. El sistema solo avisa; la persona decide.

    [Fact]
    public async Task Engine_ServiceBecomesUnresolved_StaysConfirmed_MarksChanges_AndNotifiesResponsible()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow });
        // Servicio nuevo solicitado: rompe "todo resuelto".
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        // Marcar no es cambio de estado: EvaluateAndApplyAsync devuelve false (solo cuenta curas de estado).
        var changedState = await NewEngine(ctx).EvaluateAndApplyAsync(1);

        Assert.False(changedState);
        var reserva = await ctx.Reservas.FindAsync(1);
        // NO regresa: sigue Confirmed.
        Assert.Equal(EstadoReserva.Confirmed, reserva!.Status);
        // Pero queda marcada para revisar.
        Assert.True(reserva.HasUnacknowledgedChanges);
        Assert.NotNull(reserva.ChangesPendingSince);
        var notif = Assert.Single(ctx.Notifications.Where(n => n.RelatedEntityId == 1));
        Assert.Equal("vendedor-1", notif.UserId);
        Assert.Equal("Urgent", notif.Priority);
        Assert.Equal(NotificationTypes.ReservaNeedsReview, notif.Type);
    }

    [Fact]
    public async Task Engine_NeedsReview_SameDay_DoesNotDuplicateNotification()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        var engine = NewEngine(ctx);
        await engine.EvaluateAndApplyAsync(1); // marca + notifica
        // Segunda evaluacion el mismo dia: ya esta marcada, no debe re-disparar el aviso.
        await engine.EvaluateAndApplyAsync(1);

        Assert.Single(ctx.Notifications.Where(n => n.RelatedEntityId == 1));
    }

    [Fact]
    public async Task Engine_NeedsReview_NoResponsible_FallsBackToAdmins()
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
    public async Task Engine_SetsReviewReason_OnUnresolved_AndDoesNotAutoClearOnReResolve()
    {
        // La marca y su motivo NO se limpian solos cuando los servicios se vuelven a resolver: solo los baja
        // una persona (acknowledge-changes). El sistema avisa; el dueño revisa cuando quiere.
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow });
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        var engine = NewEngine(ctx);
        await engine.EvaluateAndApplyAsync(1);

        var afterMark = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, afterMark!.Status);
        Assert.True(afterMark.HasUnacknowledgedChanges);
        Assert.False(string.IsNullOrEmpty(afterMark.LastRegressionReason));
        Assert.NotNull(afterMark.LastRegressionAt);

        // Resolvemos el servicio pendiente: la reserva ya estaba Confirmed (no hay forward), y la marca queda.
        (await ctx.HotelBookings.FindAsync(11))!.Status = "Confirmado";
        await ctx.SaveChangesAsync();
        await engine.EvaluateAndApplyAsync(1);

        var afterReResolve = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, afterReResolve!.Status);
        // La marca sigue puesta: solo la baja una persona.
        Assert.True(afterReResolve.HasUnacknowledgedChanges);
        Assert.False(string.IsNullOrEmpty(afterReResolve.LastRegressionReason));
    }

    [Fact]
    public async Task Engine_Reconciliation_SuppressesNotification_ButStillMarksChanges()
    {
        // La reconciliacion nocturna NO debe notificar (cura en lote), pero SI deja la reserva marcada.
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        await NewEngine(ctx).EvaluateAndApplyAsync(1, suppressNotifications: true);

        Assert.Empty(ctx.Notifications.Where(n => n.RelatedEntityId == 1));
        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, reserva!.Status);
        Assert.True(reserva.HasUnacknowledgedChanges);
        Assert.False(string.IsNullOrEmpty(reserva.LastRegressionReason));
    }

    // ===================== Reserva confirmada VACIADA de servicios =====================

    [Fact]
    public async Task Engine_ConfirmedReservaEmptiedOfAllServices_TransitionsToCancelled()
    {
        // ADR-048 (2026-07-17, modelo de estados derivados): una reserva Confirmed que se quedo SIN
        // servicios vivos (todos cancelados) YA NO se queda "Confirmada con cambios" para siempre — pasa
        // sola al terminal del par. Sin ninguna BookingCancellation con reembolso de operador pendiente
        // (no se sembro ninguna en este test), el terminal que corresponde es "Anulada" (Cancelled).
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        // Unico servicio: CANCELADO (no vivo). La reserva queda sin servicios activos.
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Cancelado" });
        await ctx.SaveChangesAsync();

        var changed = await NewEngine(ctx).EvaluateAndApplyAsync(1);

        Assert.True(changed);
        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Cancelled, reserva!.Status);
        // Entrar al terminal apaga la marca "confirmada con cambios" (ReservaStateCleanupRules): ya no hay
        // nada que revisar en una reserva anulada.
        Assert.False(reserva.HasUnacknowledgedChanges);

        // Rastro auditable (regla 10): la transicion automatica queda en el log, con actor "sistema".
        var logEntry = ctx.ReservaStatusChangeLogs.Single(l => l.ReservaId == 1);
        Assert.Equal(EstadoReserva.Confirmed, logEntry.FromStatus);
        Assert.Equal(EstadoReserva.Cancelled, logEntry.ToStatus);
        Assert.Equal("system:auto-state", logEntry.ByUserId); // actor sistema, no un usuario humano
        Assert.Contains("anulada", logEntry.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Engine_ConfirmedReservaWithNewUnresolvedService_StaysConfirmed_NamingTheServiceType()
    {
        // Contraste con el caso de arriba: cuando SI hay un servicio vivo sin resolver, el mensaje nombra
        // el tipo, sin caer al texto de "sin servicios activos".
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow });
        ctx.HotelBookings.Add(new HotelBooking { Id = 11, ReservaId = 1, HotelName = "H2", Status = "Solicitado" });
        await ctx.SaveChangesAsync();

        await NewEngine(ctx).EvaluateAndApplyAsync(1);

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, reserva!.Status);
        Assert.True(reserva.HasUnacknowledgedChanges);
        Assert.Contains("hotel", reserva.LastRegressionReason ?? string.Empty);
        Assert.DoesNotContain("sin servicios activos", reserva.LastRegressionReason ?? string.Empty);
    }

    // ===================== Gate manual de "En viaje": cambios sin revisar lo frenan =====================
    // 2026-06-24: al eliminar la regresion, la marca "confirmada con cambios" es la que evita que una reserva
    // confirmada avance a En viaje sin que una persona revise. Cubre el pase MANUAL (el del job esta en otro test).

    [Fact]
    public async Task UpdateStatus_ConfirmedToTraveling_WithUnacknowledgedChanges_Rejected()
    {
        await using var ctx = NewContext();
        var reserva = Reserva(1, EstadoReserva.Confirmed);
        reserva.StartDate = DateTime.UtcNow.Date;
        reserva.Balance = 0m;                       // cliente saldado: el candado de pago pasa
        reserva.HasUnacknowledgedChanges = true;    // pero hay cambios sin revisar -> debe frenar
        ctx.Reservas.Add(reserva);
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewReservaService(ctx).UpdateStatusAsync(1, EstadoReserva.Traveling));
        Assert.Contains("cambios sin revisar", ex.Message);

        // No avanzo: sigue Confirmed.
        Assert.Equal(EstadoReserva.Confirmed, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    [Fact]
    public async Task UpdateStatus_ConfirmedToTraveling_WithoutUnacknowledgedChanges_Allowed()
    {
        // Control: sin la marca, el pase manual a En viaje funciona (no rompimos el flujo normal).
        await using var ctx = NewContext();
        var reserva = Reserva(1, EstadoReserva.Confirmed);
        reserva.StartDate = DateTime.UtcNow.Date;
        reserva.Balance = 0m;
        reserva.HasUnacknowledgedChanges = false;
        ctx.Reservas.Add(reserva);
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var result = await NewReservaService(ctx).UpdateStatusAsync(1, EstadoReserva.Traveling);

        Assert.Equal(EstadoReserva.Traveling, result.Status);
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

    // ===================== FIX B2 (2026-07-04) — revertir a Budget descarta la marca "confirmada con cambios" =====

    [Fact]
    public async Task RevertToBudget_DiscardsUnacknowledgedChangesMark_AndRegression_AndDetail()
    {
        // Antes del fix, RevertStatusAsync seteaba el estado a mano y NO limpiaba la marca "confirmada con cambios":
        // volver a Presupuesto dejaba pegado el cartel "Se editaron precios...". Ahora la transición pasa por el
        // PUNTO ÚNICO, que para Budget descarta la marca + el detalle + el motivo de revisión.
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Reserva 1", Status = EstadoReserva.InManagement,
            HasUnacknowledgedChanges = true,
            ChangesPendingSince = DateTime.UtcNow.AddDays(-1),
            LastRegressionReason = "El operador cambió un precio",
            LastRegressionAt = DateTime.UtcNow.AddDays(-1),
        });
        ctx.ReservaPendingChanges.Add(new ReservaPendingChange
        {
            ReservaId = 1, ServiceType = "Hotel", ServiceDescription = "Hotel",
            Field = "SalePrice", OldValue = 100m, NewValue = 120m, Currency = "ARS",
            ChangedAt = DateTime.UtcNow.AddDays(-1),
        });
        await ctx.SaveChangesAsync();

        var result = await NewReservaService(ctx).RevertStatusAsync(
            "1",
            new RevertStatusRequest(EstadoReserva.Budget, null, "el cliente volvio a interesarse"),
            actorUserId: "admin-1", actorUserName: "Admin", actorIsAdmin: true, CancellationToken.None);

        Assert.Equal(EstadoReserva.Budget, result.Status);

        var after = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.False(after.HasUnacknowledgedChanges);
        Assert.Null(after.ChangesPendingSince);
        Assert.Null(after.LastRegressionReason);
        Assert.Null(after.LastRegressionAt);
        Assert.Equal(0, await ctx.ReservaPendingChanges.AsNoTracking().CountAsync(c => c.ReservaId == 1));
    }
}
