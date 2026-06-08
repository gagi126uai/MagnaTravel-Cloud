using System;
using System.Collections.Generic;
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
/// ADR-020 F4 (candado): el guard <see cref="ReservaLockGuard"/> y la creacion de autorizaciones
/// (<c>CreateEditAuthorizationAsync</c>). Cubre los casos exigidos en el brief: write-path bloqueado
/// sin autorizacion, permitido con autorizacion viva, autorizacion vencida re-bloquea, solo el
/// permiso correcto autoriza, cada cambio bajo autorizacion deja registro, y pagos NO bloqueados.
/// </summary>
public class Adr020LockGuardTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static Reserva Reserva(int id, string status) => new()
    {
        Id = id, NumeroReserva = $"F-{id}", Name = $"Reserva {id}", Status = status
    };

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService NewReservaService(AppDbContext context, IUserPermissionResolver? resolver = null)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
              .Returns((Reserva r) => new ReservaDto { PublicId = r.PublicId, Name = r.Name, Status = r.Status });
        mapper.Setup(m => m.Map<PaymentDto>(It.IsAny<Payment>()))
              .Returns((Payment p) => new PaymentDto { Amount = p.Amount });
        return new ReservaService(context, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance, resolver);
    }

    // ===================== Guard directo =====================

    [Theory]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]
    [InlineData(EstadoReserva.ToSettle, true)]
    [InlineData(EstadoReserva.Closed, true)]
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.InManagement, false)]
    public void IsLockedStatus_LocksFromConfirmedOnward(string status, bool expectedLocked)
    {
        Assert.Equal(expectedLocked, ReservaLockGuard.IsLockedStatus(status));
    }

    [Fact]
    public async Task Guard_LockedReserva_WithoutAuthorization_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReservaLockGuard.EnsureCanEditAsync(ctx, 1,
                ReservaEditAuthorizationOperations.ServiceEdited, "u1", "User", "HotelBooking", 10, "x"));
    }

    [Fact]
    public async Task Guard_UnlockedReserva_AllowsAndRecordsNothing()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement));
        await ctx.SaveChangesAsync();

        var result = await ReservaLockGuard.EnsureCanEditAsync(ctx, 1,
            ReservaEditAuthorizationOperations.ServiceEdited, "u1", "User", "HotelBooking", 10, "x");
        await ctx.SaveChangesAsync();

        Assert.Null(result);
        Assert.Empty(ctx.ReservaEditAuthorizationChanges);
    }

    [Fact]
    public async Task Guard_LockedReserva_WithLiveAuthorization_AllowsAndRecordsChange()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 5, ReservaId = 1, Reason = "edicion autorizada por admin",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        });
        await ctx.SaveChangesAsync();

        var result = await ReservaLockGuard.EnsureCanEditAsync(ctx, 1,
            ReservaEditAuthorizationOperations.ServiceDeleted, "u1", "User", "HotelBooking", 10, "borra hotel");
        await ctx.SaveChangesAsync();

        Assert.NotNull(result);
        var change = Assert.Single(ctx.ReservaEditAuthorizationChanges);
        Assert.Equal(ReservaEditAuthorizationOperations.ServiceDeleted, change.Operation);
        Assert.Equal(5, change.AuthorizationId);
        Assert.Equal("u1", change.PerformedByUserId);
    }

    [Fact]
    public async Task Guard_ExpiredAuthorization_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 5, ReservaId = 1, Reason = "autorizacion ya vencida",
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1) // vencida
        });
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReservaLockGuard.EnsureCanEditAsync(ctx, 1,
                ReservaEditAuthorizationOperations.ServiceEdited, "u1", "User", "HotelBooking", 10, "x"));
    }

    // ===================== Write-path real (servicio generico) =====================

    [Fact]
    public async Task RemoveService_OnConfirmedReserva_WithoutAuthorization_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        ctx.Servicios.Add(new ServicioReserva { Id = 7, ReservaId = 1, ServiceType = "Otro", Description = "x" });
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewReservaService(ctx).RemoveServiceAsync("7"));
    }

    [Fact]
    public async Task AddPayment_OnConfirmedReserva_NotBlockedByLock()
    {
        // (f) Pagos NO estan bajo candado: cobrar una reserva confirmada es flujo normal.
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        await ctx.SaveChangesAsync();

        var dto = await NewReservaService(ctx).AddPaymentAsync(1, new Payment { Amount = 100m, Method = "Efectivo" });

        Assert.Equal(100m, dto.Amount);
        Assert.Single(ctx.Payments);
    }

    // ===================== CreateEditAuthorizationAsync =====================

    [Fact]
    public async Task CreateAuth_AdminSelfAuthorizes_PersistsLiveAuthorization()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        await ctx.SaveChangesAsync();

        var dto = await NewReservaService(ctx).CreateEditAuthorizationAsync(
            "1", new CreateEditAuthorizationRequest("editar datos por pedido del cliente", null),
            actorUserId: "admin-1", actorUserName: "Admin", actorIsAdmin: true, CancellationToken.None);

        Assert.Equal("admin-1", dto.AuthorizedByUserId);
        Assert.Equal(EstadoReserva.Confirmed, dto.ReservaStatusSnapshot);
        Assert.True(dto.ExpiresAt > DateTime.UtcNow);
        Assert.Single(ctx.ReservaEditAuthorizations);
    }

    [Fact]
    public async Task CreateAuth_NotLockedReserva_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement));
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewReservaService(ctx).CreateEditAuthorizationAsync(
                "1", new CreateEditAuthorizationRequest("motivo suficientemente largo", null),
                "admin-1", "Admin", actorIsAdmin: true, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAuth_ShortReason_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewReservaService(ctx).CreateEditAuthorizationAsync(
                "1", new CreateEditAuthorizationRequest("corto", null),
                "admin-1", "Admin", actorIsAdmin: true, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAuth_NonAdminWithoutAuthorizer_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        await ctx.SaveChangesAsync();

        var resolver = new Mock<IUserPermissionResolver>();
        resolver.Setup(r => r.GetPermissionsAsync("vendedor-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewReservaService(ctx, resolver.Object).CreateEditAuthorizationAsync(
                "1", new CreateEditAuthorizationRequest("necesito editar la reserva ya", null),
                "vendedor-1", "Vendedor", actorIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAuth_AuthorizerWithoutPermission_Throws()
    {
        // (d) solo quien tiene reservas.authorize_locked_edit puede autorizar.
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        ctx.Users.Add(new ApplicationUser { Id = "sup-1", UserName = "sup", Email = "s@s.com", IsActive = true });
        ctx.Roles.Add(new IdentityRole { Id = "role-vend", Name = "Vendedor", NormalizedName = "VENDEDOR" });
        ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = "sup-1", RoleId = "role-vend" });
        await ctx.SaveChangesAsync();

        var resolver = new Mock<IUserPermissionResolver>();
        resolver.Setup(r => r.GetPermissionsAsync("vendedor-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewReservaService(ctx, resolver.Object).CreateEditAuthorizationAsync(
                "1", new CreateEditAuthorizationRequest("necesito editar la reserva ya", "sup-1"),
                "vendedor-1", "Vendedor", actorIsAdmin: false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAuth_AuthorizerWithPermission_Succeeds()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        ctx.Users.Add(new ApplicationUser { Id = "sup-1", UserName = "sup", Email = "s@s.com", IsActive = true });
        ctx.Roles.Add(new IdentityRole { Id = "role-bo", Name = "BackOffice", NormalizedName = "BACKOFFICE" });
        ctx.UserRoles.Add(new IdentityUserRole<string> { UserId = "sup-1", RoleId = "role-bo" });
        ctx.RolePermissions.Add(new RolePermission { RoleName = "BackOffice", Permission = Permissions.ReservasAuthorizeLockedEdit });
        await ctx.SaveChangesAsync();

        var resolver = new Mock<IUserPermissionResolver>();
        resolver.Setup(r => r.GetPermissionsAsync("vendedor-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());

        var dto = await NewReservaService(ctx, resolver.Object).CreateEditAuthorizationAsync(
            "1", new CreateEditAuthorizationRequest("necesito editar la reserva ya", "sup-1"),
            "vendedor-1", "Vendedor", actorIsAdmin: false, CancellationToken.None);

        Assert.Equal("sup-1", dto.AuthorizedByUserId);
        Assert.Equal("vendedor-1", dto.RequestedByUserId);
    }

    [Fact]
    public async Task CreateAuth_NewAuthorization_ExpiresPreviousLiveOne()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        await ctx.SaveChangesAsync();
        var svc = NewReservaService(ctx);

        var first = await svc.CreateEditAuthorizationAsync(
            "1", new CreateEditAuthorizationRequest("primera autorizacion de edicion", null),
            "admin-1", "Admin", true, CancellationToken.None);
        var second = await svc.CreateEditAuthorizationAsync(
            "1", new CreateEditAuthorizationRequest("segunda autorizacion de edicion", null),
            "admin-1", "Admin", true, CancellationToken.None);

        // Una sola viva: la primera quedo expirada, solo la segunda sigue vigente.
        var now = DateTime.UtcNow;
        var liveCount = await ctx.ReservaEditAuthorizations.CountAsync(a => a.ReservaId == 1 && a.ExpiresAt > now);
        Assert.Equal(1, liveCount);
        Assert.NotEqual(first.PublicId, second.PublicId);
    }

    // ===================== End-to-end =====================

    [Fact]
    public async Task AuthorizeThenEdit_AllowsWriteAndRecordsChange()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        ctx.Servicios.Add(new ServicioReserva { Id = 7, ReservaId = 1, ServiceType = "Otro", Description = "x" });
        await ctx.SaveChangesAsync();
        var svc = NewReservaService(ctx);

        // Admin destraba la reserva, luego el borrado del servicio pasa y deja registro.
        await svc.CreateEditAuthorizationAsync(
            "1", new CreateEditAuthorizationRequest("hay que sacar un servicio cargado de mas", null),
            "admin-1", "Admin", true, CancellationToken.None);

        await svc.RemoveServiceAsync("7");

        Assert.Empty(ctx.Servicios);
        var change = Assert.Single(ctx.ReservaEditAuthorizationChanges);
        Assert.Equal(ReservaEditAuthorizationOperations.ServiceDeleted, change.Operation);
        Assert.Equal(7, change.EntityId);
    }
}
