using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Fuga 1 (ADR-017 §2.7, F1b — fix de seguridad SIN flag): regresion del leak de costos
/// del tarifario. Antes GET /api/rates, /search y /{publicId} devolvian NetCost/Tax
/// (y Commission en los listados) a CUALQUIER usuario logueado. Contrato fijado:
///   - usuario SIN cobranzas.see_cost -> NetCost/Tax/Commission enmascarados a 0m;
///   - usuario CON cobranzas.see_cost (o Admin) -> valores reales, sin regresion;
///   - SalePrice viaja SIEMPRE (decision D1 del dueño: quien no ve costos ve la venta);
///   - lo persistido en DB nunca se altera.
/// </summary>
public class RateServiceCostMaskingTests
{
    private const string SeeCostPermission = Permissions.CobranzasSeeCost;

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // Construye el servicio con un caller no-Admin. Si "canSeeCost" es true, el
    // resolver devuelve el permiso cobranzas.see_cost; si es false, devuelve vacio.
    private static RateService CreateServiceForUser(AppDbContext context, bool canSeeCost, bool isAdmin = false)
    {
        const string userId = "vendedor-test";
        var accessor = isAdmin
            ? BuildHttpContextAccessor(userId, "Admin")
            : BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, SeeCostPermission)
            : BuildResolver(userId); // sin permisos

        return new RateService(context, NullLogger<RateService>.Instance, resolver, accessor);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    // Seed: un proveedor + una tarifa de hotel con costo/impuesto/ganancia conocidos.
    private static async Task<Rate> SeedRateAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Mayorista test" };
        var rate = new Rate
        {
            Id = 1,
            SupplierId = supplier.Id,
            ServiceType = "Hotel",
            ProductName = "Hotel Maitei doble",
            HotelName = "Hotel Maitei",
            City = "Posadas",
            RoomType = "Doble",
            MealPlan = "Desayuno",
            NetCost = 100m,
            Tax = 15m,
            SalePrice = 160m,
            Commission = 45m,
            IsActive = true
        };
        context.Suppliers.Add(supplier);
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return rate;
    }

    // ============================= GET /api/rates/search =============================

    [Fact]
    public async Task SearchAsync_UserWithoutSeeCost_MasksNetCostAndTax_KeepsSalePrice()
    {
        await using var context = CreateContext();
        await SeedRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var results = await service.SearchAsync(supplierId: null, serviceType: null, query: null, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(0m, item.NetCost);   // costo oculto
        Assert.Equal(0m, item.Tax);       // impuesto = componente del costo, tambien oculto
        Assert.Equal(160m, item.SalePrice); // la venta viaja SIEMPRE (D1)

        // Lo persistido no se altera: solo se anula en el DTO de salida.
        var stored = await context.Rates.AsNoTracking().SingleAsync();
        Assert.Equal(100m, stored.NetCost);
        Assert.Equal(15m, stored.Tax);
    }

    [Fact]
    public async Task SearchAsync_UserWithSeeCost_KeepsCosts()
    {
        await using var context = CreateContext();
        await SeedRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var results = await service.SearchAsync(supplierId: null, serviceType: null, query: null, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(100m, item.NetCost);
        Assert.Equal(15m, item.Tax);
        Assert.Equal(160m, item.SalePrice);
    }

    [Fact]
    public async Task SearchAsync_AdminWithoutExplicitPermission_KeepsCosts()
    {
        await using var context = CreateContext();
        await SeedRateAsync(context);
        // Admin sin el permiso en el resolver: el bypass por rol debe alcanzar.
        var service = CreateServiceForUser(context, canSeeCost: false, isAdmin: true);

        var results = await service.SearchAsync(supplierId: null, serviceType: null, query: null, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(100m, item.NetCost);
        Assert.Equal(15m, item.Tax);
    }

    // ============================= GET /api/rates (listado) =============================

    [Fact]
    public async Task GetAllAsync_UserWithoutSeeCost_MasksNetCostTaxAndCommission()
    {
        await using var context = CreateContext();
        await SeedRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var page = await service.GetAllAsync(new RateListQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(0m, item.NetCost);
        Assert.Equal(0m, item.Tax);
        Assert.Equal(0m, item.Commission); // la ganancia revela el margen: tambien se oculta
        Assert.Equal(160m, item.SalePrice);
    }

    [Fact]
    public async Task GetAllAsync_UserWithSeeCost_KeepsCosts()
    {
        await using var context = CreateContext();
        await SeedRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var page = await service.GetAllAsync(new RateListQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(100m, item.NetCost);
        Assert.Equal(15m, item.Tax);
        Assert.Equal(45m, item.Commission);
    }

    // ============================= GET /api/rates/{publicId} =============================

    [Fact]
    public async Task GetByPublicIdAsync_UserWithoutSeeCost_MasksCosts()
    {
        await using var context = CreateContext();
        var rate = await SeedRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var item = await service.GetByPublicIdAsync(rate.PublicId.ToString(), CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal(0m, item!.NetCost);
        Assert.Equal(0m, item.Tax);
        Assert.Equal(0m, item.Commission);
        Assert.Equal(160m, item.SalePrice);
    }

    [Fact]
    public async Task GetByPublicIdAsync_UserWithSeeCost_KeepsCosts()
    {
        await using var context = CreateContext();
        var rate = await SeedRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var item = await service.GetByPublicIdAsync(rate.PublicId.ToString(), CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal(100m, item!.NetCost);
        Assert.Equal(15m, item.Tax);
        Assert.Equal(45m, item.Commission);
    }

    // ============================= GET /api/rates/hotels (grupos) =============================

    [Fact]
    public async Task GetHotelGroupsAsync_UserWithoutSeeCost_MasksCostsInGroupItems()
    {
        await using var context = CreateContext();
        await SeedRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var page = await service.GetHotelGroupsAsync(new HotelRateGroupsQuery(), CancellationToken.None);

        var group = Assert.Single(page.Items);
        // FromPrice es un MIN de SalePrice: NO se enmascara (D1).
        Assert.Equal(160m, group.FromPrice);
        var item = Assert.Single(group.Items);
        Assert.Equal(0m, item.NetCost);
        Assert.Equal(0m, item.Tax);
        Assert.Equal(0m, item.Commission);
        Assert.Equal(160m, item.SalePrice);
    }

    // ============================= GET /api/rates/groups =============================

    [Fact]
    public async Task GetGroupsAsync_UserWithoutSeeCost_MasksCostsInGroupItems()
    {
        await using var context = CreateContext();
        await SeedRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var page = await service.GetGroupsAsync(new RateGroupsQuery(), CancellationToken.None);

        var group = Assert.Single(page.Items);
        // FromPrice es un MIN de SalePrice: NO se enmascara (D1).
        Assert.Equal(160m, group.FromPrice);
        var item = Assert.Single(group.Items);
        Assert.Equal(0m, item.NetCost);
        Assert.Equal(0m, item.Tax);
        Assert.Equal(0m, item.Commission);
        Assert.Equal(160m, item.SalePrice);
    }

    // ============================= fail-closed =============================

    [Fact]
    public async Task SearchAsync_WithoutResolverNorAccessor_FailClosed_MasksCosts()
    {
        await using var context = CreateContext();
        await SeedRateAsync(context);
        // Instancia "legacy" sin resolver ni accessor (el ctor de 2 args): no hay forma
        // de saber quien llama -> fail-closed, los costos se ocultan SIEMPRE.
        var service = new RateService(context, NullLogger<RateService>.Instance);

        var results = await service.SearchAsync(supplierId: null, serviceType: null, query: null, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(0m, item.NetCost);
        Assert.Equal(0m, item.Tax);
        Assert.Equal(160m, item.SalePrice); // la venta viaja igual (D1)
    }
}
