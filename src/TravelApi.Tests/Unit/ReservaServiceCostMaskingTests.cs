using System;
using System.Collections.Generic;
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
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 2a (Decision 4) — Mascara de costos para roles sin
/// cobranzas.see_cost. Pinea: vendedor NO ve costos del proveedor.
/// </summary>
public class ReservaServiceCostMaskingTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceCostMaskingTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
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

    private static IHttpContextAccessor BuildContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
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
        => new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(),
               NullLogger<ReservaService>.Instance, resolver, accessor);

    private static async Task SeedReservaWithCosts(AppDbContext ctx)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva con costos",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-1",
            TotalCost = 500m,
            TotalSale = 800m
        });
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 10,
            ReservaId = 1,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Test",
            ConfirmationNumber = "CN-1",
            Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = 800m,
            NetCost = 500m,
            Commission = 300m,
            CreatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Detail_user_with_see_cost_sees_costs()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithCosts(ctx);
        var accessor = BuildContextAccessor("colab-1", "Colaborador");
        var resolver = BuildResolver("colab-1", Permissions.CobranzasSeeCost);
        var service = BuildService(ctx, accessor, resolver);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        Assert.Equal(500m, dto.TotalCost);
        Assert.Single(dto.Servicios);
        Assert.Equal(500m, dto.Servicios[0].NetCost);
        Assert.Equal(300m, dto.Servicios[0].Commission);
    }

    [Fact]
    public async Task Detail_user_without_see_cost_has_costs_masked_to_zero()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithCosts(ctx);
        var accessor = BuildContextAccessor("vendedor-1", "Vendedor");
        var resolver = BuildResolver("vendedor-1"); // sin see_cost
        var service = BuildService(ctx, accessor, resolver);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        // SalePrice se preserva; cost/commission se enmascaran a 0.
        Assert.Equal(0m, dto.TotalCost);
        Assert.Single(dto.Servicios);
        Assert.Equal(800m, dto.Servicios[0].SalePrice);
        Assert.Equal(0m, dto.Servicios[0].NetCost);
        Assert.Equal(0m, dto.Servicios[0].Commission);
    }

    [Fact]
    public async Task Detail_admin_bypasses_masking()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithCosts(ctx);
        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1"); // sin permisos explicitos
        var service = BuildService(ctx, accessor, resolver);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        Assert.Equal(500m, dto.TotalCost);
        Assert.Equal(500m, dto.Servicios[0].NetCost);
    }

    [Fact]
    public async Task List_user_without_see_cost_has_TotalCost_masked()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithCosts(ctx);
        var accessor = BuildContextAccessor("vendedor-1", "Vendedor");
        var resolver = BuildResolver("vendedor-1", Permissions.ReservasViewAll); // ve todas, pero NO cost
        var service = BuildService(ctx, accessor, resolver);

        var (page, scope) = await service.GetReservasWithScopeAsync(new ReservaListQuery(), CancellationToken.None);

        Assert.Equal("all", scope);
        Assert.Single(page.Items);
        Assert.Equal(0m, page.Items[0].TotalCost);
        Assert.Equal(800m, page.Items[0].TotalSale); // venta NO se enmascara
    }

    [Fact]
    public async Task List_user_with_see_cost_sees_costs()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithCosts(ctx);
        var accessor = BuildContextAccessor("colab-1", "Colaborador");
        var resolver = BuildResolver("colab-1", Permissions.ReservasViewAll, Permissions.CobranzasSeeCost);
        var service = BuildService(ctx, accessor, resolver);

        var (page, _) = await service.GetReservasWithScopeAsync(new ReservaListQuery(), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(500m, page.Items[0].TotalCost);
    }
}
