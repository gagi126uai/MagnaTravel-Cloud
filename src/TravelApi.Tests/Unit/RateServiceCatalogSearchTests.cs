using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.2 (catalogo find-or-create, buscador): tests UNITARIOS de <c>catalog-search</c>.
///
/// <para>Estos tests corren sobre EF Core InMemory, asi que NO ejercitan pg_trgm (la similitud difusa
/// real solo se prueba contra Postgres en el VPS — R5 de integracion). InMemory dispara el fallback
/// LINQ por substring del service, que alcanza para verificar todo el resto del pipeline:
/// gate por flag (R4), enmascarado de costo (R1) y dedupe (R5/m1).</para>
/// </summary>
public class RateServiceCatalogSearchTests
{
    private const string SeeCostPermission = Permissions.CobranzasSeeCost;

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // Construye el service con el flag prendido/apagado y el permiso de costos a eleccion.
    private static RateService CreateService(
        AppDbContext context,
        bool catalogEnabled,
        bool canSeeCost = true,
        bool isAdmin = false,
        bool withIdentity = true)
    {
        const string userId = "vendedor-test";
        IHttpContextAccessor? accessor = null;
        IUserPermissionResolver? resolver = null;
        if (withIdentity)
        {
            accessor = isAdmin
                ? BuildHttpContextAccessor(userId, "Admin")
                : BuildHttpContextAccessor(userId);
            resolver = canSeeCost
                ? BuildResolver(userId, SeeCostPermission)
                : BuildResolver(userId);
        }

        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = catalogEnabled });

        return new RateService(
            context, NullLogger<RateService>.Instance, resolver, accessor, settings.Object);
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

    // Crea un Rate de hotel con SearchName ya normalizado (como lo deja el backfill / la app).
    private static Rate BuildHotelRate(
        int id, string hotelName, string city, decimal netCost = 100m, decimal salePrice = 160m)
    {
        return new Rate
        {
            Id = id,
            ServiceType = "Hotel",
            ProductName = $"Tarifa {hotelName}",
            HotelName = hotelName,
            City = city,
            RoomType = "Doble",
            NetCost = netCost,
            Tax = 15m,
            SalePrice = salePrice,
            Commission = salePrice - netCost - 15m,
            Currency = "ARS",
            PriceUnit = "noche",
            HotelPriceType = "base_doble",
            IsActive = true,
            // SearchName se calcula con la MISMA funcion autoritativa que usa la app.
            SearchName = TextNormalizer.NormalizeForCatalog(hotelName)
        };
    }

    // ============================= R4 — gate por flag =============================

    [Fact]
    public async Task CatalogSearch_FlagOff_ReturnsNull()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: false);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        // null = "el endpoint no existe" -> el controller responde 404 (R4).
        Assert.Null(result);
    }

    [Fact]
    public async Task CatalogSearch_FlagOn_ReturnsResults()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        Assert.NotNull(result);
        var item = Assert.Single(result!);
        Assert.Equal("Hotel Maitei", item.Name);
        Assert.Equal("Posadas", item.Subtitle);
    }

    [Fact]
    public async Task CatalogSearch_NoSettingsService_FailClosed_ReturnsNull()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        await context.SaveChangesAsync();
        // Ctor legacy SIN settings service: no hay forma de leer el flag -> fail-closed (404).
        var service = new RateService(context, NullLogger<RateService>.Instance);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CatalogSearch_QueryTooShort_ReturnsEmpty_NotNull()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "m", CancellationToken.None);

        // Con flag ON pero q corta: lista vacia (NO null: el endpoint existe, solo no hay que buscar).
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    [Fact]
    public async Task CatalogSearch_FiltersByServiceType()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei", "Posadas"));
        context.Rates.Add(new Rate
        {
            Id = 2,
            ServiceType = "Aereo",
            ProductName = "Maitei Air EZE-MIA",
            Origin = "EZE",
            Destination = "MIA",
            SalePrice = 500m,
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog("Maitei Air EZE-MIA")
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Equal("Hotel", item.ServiceType);
    }

    // ============================= R5 / m1 — dedupe =============================

    [Fact]
    public async Task CatalogSearch_SameHotelLoadedManyTimes_ReturnsOneResult()
    {
        await using var context = CreateContext();
        // Tres tarifas del MISMO hotel (distinto room type) -> un solo producto en el dropdown.
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        var second = BuildHotelRate(2, "Hotel Maitei", "Posadas");
        second.RoomType = "Triple";
        context.Rates.Add(second);
        var third = BuildHotelRate(3, "Hotel Maitei", "Posadas");
        third.RoomType = "Suite";
        context.Rates.Add(third);
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        Assert.Single(result!);
    }

    [Fact]
    public async Task CatalogSearch_HomonymHotelsDifferentCities_ReturnsTwoResults()
    {
        await using var context = CreateContext();
        // Dos hoteles homonimos en ciudades distintas = dos productos distintos (m1).
        context.Rates.Add(BuildHotelRate(1, "Costanera", "Posadas"));
        context.Rates.Add(BuildHotelRate(2, "Costanera", "Córdoba"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "costanera", CancellationToken.None);

        Assert.Equal(2, result!.Count);
        Assert.Contains(result!, item => item.Subtitle == "Posadas");
        Assert.Contains(result!, item => item.Subtitle == "Córdoba");
    }

    // ============================= R1 — enmascarado de costo =============================

    [Fact]
    public async Task CatalogSearch_WithoutSeeCost_MasksNetCost_KeepsSalePrice_RateFallback()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas", netCost: 100m, salePrice: 160m));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: false);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        // Sin ventas registradas -> viene el rateFallback (no el lastSale).
        Assert.Null(item.LastSale);
        Assert.NotNull(item.RateFallback);
        Assert.Null(item.RateFallback!.NetCost);     // costo oculto (R1/D1)
        Assert.Equal(160m, item.RateFallback.SalePrice); // la venta viaja SIEMPRE
    }

    [Fact]
    public async Task CatalogSearch_WithSeeCost_KeepsNetCost_RateFallback()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas", netCost: 100m, salePrice: 160m));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.NotNull(item.RateFallback);
        Assert.Equal(100m, item.RateFallback!.NetCost);
        Assert.Equal(160m, item.RateFallback.SalePrice);
    }

    [Fact]
    public async Task CatalogSearch_AdminWithoutExplicitPermission_KeepsNetCost()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas", netCost: 100m, salePrice: 160m));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: false, isAdmin: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Equal(100m, item.RateFallback!.NetCost); // bypass por rol Admin
    }

    [Fact]
    public async Task CatalogSearch_WithoutIdentity_FailClosed_MasksNetCost()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas", netCost: 100m, salePrice: 160m));
        await context.SaveChangesAsync();
        // Flag ON pero sin resolver ni accessor: no se sabe quien llama -> fail-closed (oculta costo).
        var service = CreateService(context, catalogEnabled: true, withIdentity: false);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Null(item.RateFallback!.NetCost);
        Assert.Equal(160m, item.RateFallback.SalePrice);
    }

    // ============================= contexto "ultima vez" =============================

    [Fact]
    public async Task CatalogSearch_WithLastSale_ReturnsLastSaleContext_NotFallback()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Id = 1, Name = "Ola Mayorista" };
        context.Suppliers.Add(supplier);
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        context.RateSupplierSales.Add(new RateSupplierSale
        {
            Id = 1,
            RateId = 1,
            SupplierId = 1,
            LastSoldAt = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            LastNetCost = 48000m,
            LastTax = 0m,
            LastSalePrice = 60000m,
            LastCurrency = "ARS",
            LastPriceUnit = "noche_habitacion",
            SalesCount = 3
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Null(item.RateFallback);            // habiendo venta, NO viene el fallback
        Assert.NotNull(item.LastSale);
        Assert.Equal("Ola Mayorista", item.LastSale!.SupplierName);
        Assert.Equal(48000m, item.LastSale.NetCost);
        Assert.Equal(60000m, item.LastSale.SalePrice);
        Assert.Equal("noche_habitacion", item.LastSale.PriceUnit);
    }

    [Fact]
    public async Task CatalogSearch_WithLastSale_WithoutSeeCost_MasksLastSaleNetCost()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola Mayorista" });
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        context.RateSupplierSales.Add(new RateSupplierSale
        {
            Id = 1,
            RateId = 1,
            SupplierId = 1,
            LastSoldAt = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            LastNetCost = 48000m,
            LastSalePrice = 60000m,
            LastCurrency = "ARS",
            LastPriceUnit = "noche_habitacion",
            SalesCount = 1
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: false);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.NotNull(item.LastSale);
        Assert.Null(item.LastSale!.NetCost);        // costo oculto (R1/D1)
        Assert.Equal(60000m, item.LastSale.SalePrice); // venta visible
    }
}
