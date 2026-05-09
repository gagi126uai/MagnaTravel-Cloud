using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 2a — Filtro automatico "mias" en GetReservasWithScopeAsync.
/// Pinea Decision 11 de Gaston: si el usuario tiene reservas.view_all, ve
/// todas; sino, solo las que son suyas (ResponsibleUserId == currentUserId).
/// </summary>
public class ReservaServiceFilteringTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IMapper> _mapperMock;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceFilteringTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapperMock = new Mock<IMapper>();
        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static IHttpContextAccessor BuildContextAccessor(string? userId, params string[] roles)
    {
        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(userId))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }
        foreach (var r in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, r));
        }
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        var principal = new ClaimsPrincipal(identity);
        var ctx = new DefaultHttpContext { User = principal };
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return accessor;
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(set);
        return mock.Object;
    }

    private ReservaService BuildService(AppDbContext context, IHttpContextAccessor accessor, IUserPermissionResolver resolver)
        => new(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(),
               NullLogger<ReservaService>.Instance, resolver, accessor);

    private static async Task SeedReservas(AppDbContext ctx)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-0001", Name = "Reserva A", Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-1"
        });
        ctx.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "F-2026-0002", Name = "Reserva B", Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-2"
        });
        ctx.Reservas.Add(new Reserva
        {
            Id = 3, NumeroReserva = "F-2026-0003", Name = "Reserva C", Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-1"
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Vendedor_without_view_all_only_sees_own_reservas()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservas(ctx);
        var accessor = BuildContextAccessor("vendedor-1", "Vendedor");
        var resolver = BuildResolver("vendedor-1"); // sin view_all
        var service = BuildService(ctx, accessor, resolver);

        var (page, scope) = await service.GetReservasWithScopeAsync(new ReservaListQuery(), CancellationToken.None);

        Assert.Equal("mine", scope);
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, r => Assert.Equal("vendedor-1", r.ResponsibleUserId));
    }

    [Fact]
    public async Task User_with_view_all_sees_all_reservas()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservas(ctx);
        var accessor = BuildContextAccessor("colab-1", "Colaborador");
        var resolver = BuildResolver("colab-1", Permissions.ReservasViewAll);
        var service = BuildService(ctx, accessor, resolver);

        var (page, scope) = await service.GetReservasWithScopeAsync(new ReservaListQuery(), CancellationToken.None);

        Assert.Equal("all", scope);
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task Admin_role_sees_all_regardless_of_permissions()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservas(ctx);
        // Admin sin permisos explicitos en el resolver — el service hace bypass por rol.
        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        var service = BuildService(ctx, accessor, resolver);

        var (page, scope) = await service.GetReservasWithScopeAsync(new ReservaListQuery(), CancellationToken.None);

        Assert.Equal("all", scope);
        Assert.Equal(3, page.TotalCount);
    }

    [Fact]
    public async Task Unauthenticated_user_falls_back_to_mine_scope_with_no_results()
    {
        // Caso edge: NameIdentifier null pero queremos fail-safe (no hay user, no
        // hay datos). El service filtra por un sentinel imposible.
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservas(ctx);
        var accessor = BuildContextAccessor(userId: null);
        var resolver = BuildResolver("unused");
        var service = BuildService(ctx, accessor, resolver);

        var (page, scope) = await service.GetReservasWithScopeAsync(new ReservaListQuery(), CancellationToken.None);

        Assert.Equal("mine", scope);
        Assert.Equal(0, page.TotalCount);
    }
}
