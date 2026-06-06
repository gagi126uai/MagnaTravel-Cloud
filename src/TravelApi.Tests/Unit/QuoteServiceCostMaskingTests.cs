using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B3 (ADR-017 §2.7, F1b — fix de seguridad SIN flag): el modulo Quotes era un bypass
/// total del masking del tarifario (un "oraculo" del NetCost de cualquier tarifa):
///   - GET /api/quotes exponia TotalCost/GrossMargin a cualquier usuario logueado;
///   - GET /api/quotes/{id} exponia UnitCost/MarkupPercent/TotalCost de cada item;
///   - POST /api/quotes/{id}/items con rateId autocompletaba UnitCost = rate.NetCost
///     y lo DEVOLVIA en el response.
/// Contrato fijado:
///   - caller SIN cobranzas.see_cost -> costos/margenes en 0m, MISMA forma de DTO;
///   - caller CON permiso (o Admin) -> valores reales, sin regresion;
///   - la venta (TotalSale/UnitPrice/TotalPrice) viaja SIEMPRE (D1);
///   - el autocompletado server-side SIGUE guardando el costo real en DB.
/// </summary>
public class QuoteServiceCostMaskingTests
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
    private static QuoteService CreateServiceForUser(AppDbContext context, bool canSeeCost, bool isAdmin = false)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "vendedor-test";
        var accessor = isAdmin
            ? BuildHttpContextAccessor(userId, "Admin")
            : BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, SeeCostPermission)
            : BuildResolver(userId); // sin permisos

        return new QuoteService(
            context,
            Mock.Of<IEntityReferenceResolver>(),
            settings.Object,
            resolver,
            accessor);
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

    // Seed: cotizacion con un item de costo conocido + una tarifa para el autocompletado.
    // Item: UnitCost 100, UnitPrice 160, Markup 60%. Totales: costo 100, venta 160, margen 60.
    private static async Task<(Quote quote, Rate rate)> SeedQuoteAndRateAsync(AppDbContext context)
    {
        var quote = new Quote
        {
            Id = 1,
            QuoteNumber = "COT-00001",
            Title = "Cotizacion test",
            TotalCost = 100m,
            TotalSale = 160m,
            GrossMargin = 60m
        };
        var item = new QuoteItem
        {
            Id = 1,
            QuoteId = 1,
            ServiceType = "Hotel",
            Description = "Hotel test",
            Quantity = 1,
            UnitCost = 100m,
            UnitPrice = 160m,
            MarkupPercent = 60m
        };
        var rate = new Rate
        {
            Id = 1,
            ServiceType = "Excursion",
            ProductName = "Excursion glaciar",
            NetCost = 70m,
            SalePrice = 120m,
            Commission = 50m,
            IsActive = true
        };
        context.Quotes.Add(quote);
        context.QuoteItems.Add(item);
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return (quote, rate);
    }

    // ============================= GET /api/quotes (listado) =============================

    [Fact]
    public async Task GetAllAsync_UserWithoutSeeCost_MasksTotalCostAndGrossMargin()
    {
        await using var context = CreateContext();
        await SeedQuoteAndRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var summaries = await service.GetAllAsync(CancellationToken.None);

        var summary = Assert.Single(summaries);
        Assert.Equal(0m, summary.TotalCost);    // costo oculto
        Assert.Equal(0m, summary.GrossMargin);  // el margen revela el costo dado el precio
        Assert.Equal(160m, summary.TotalSale);  // la venta viaja SIEMPRE (D1)
    }

    [Fact]
    public async Task GetAllAsync_UserWithSeeCost_KeepsCosts()
    {
        await using var context = CreateContext();
        await SeedQuoteAndRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var summaries = await service.GetAllAsync(CancellationToken.None);

        var summary = Assert.Single(summaries);
        Assert.Equal(100m, summary.TotalCost);
        Assert.Equal(60m, summary.GrossMargin);
        Assert.Equal(160m, summary.TotalSale);
    }

    // ============================= GET /api/quotes/{id} =============================

    [Fact]
    public async Task GetByIdAsync_UserWithoutSeeCost_MasksItemCosts()
    {
        await using var context = CreateContext();
        var (quote, _) = await SeedQuoteAndRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var detail = await service.GetByIdAsync(quote.PublicId.ToString(), CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(0m, detail!.TotalCost);
        Assert.Equal(0m, detail.GrossMargin);
        var item = Assert.Single(detail.Items);
        Assert.Equal(0m, item.UnitCost);
        Assert.Equal(0m, item.TotalCost);
        Assert.Equal(0m, item.MarkupPercent); // con el markup y la venta se despeja el costo
        Assert.Equal(160m, item.UnitPrice);   // la venta viaja SIEMPRE (D1)
        Assert.Equal(160m, item.TotalPrice);
    }

    [Fact]
    public async Task GetByIdAsync_AdminWithoutExplicitPermission_KeepsCosts()
    {
        await using var context = CreateContext();
        var (quote, _) = await SeedQuoteAndRateAsync(context);
        // Admin sin el permiso en el resolver: el bypass por rol debe alcanzar.
        var service = CreateServiceForUser(context, canSeeCost: false, isAdmin: true);

        var detail = await service.GetByIdAsync(quote.PublicId.ToString(), CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(100m, detail!.TotalCost);
        var item = Assert.Single(detail.Items);
        Assert.Equal(100m, item.UnitCost);
        Assert.Equal(60m, item.MarkupPercent);
    }

    // ============================= POST /api/quotes/{id}/items =============================

    [Fact]
    public async Task AddItemAsync_FromRate_UserWithoutSeeCost_NoEchoOfUnitCost_DbKeepsRealCost()
    {
        await using var context = CreateContext();
        var (quote, rate) = await SeedQuoteAndRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var detail = await service.AddItemAsync(
            quote.PublicId.ToString(),
            new UpsertQuoteItemRequest
            {
                ServiceType = "Excursion",
                Description = "lo completa la tarifa",
                Quantity = 1,
                UnitCost = 0m, // el caller no ve costos: manda 0
                UnitPrice = 0m,
                RateId = rate.PublicId.ToString()
            },
            CancellationToken.None);

        // El response NO hace echo del UnitCost autocompletado (era el oraculo de costos).
        Assert.All(detail.Items, item =>
        {
            Assert.Equal(0m, item.UnitCost);
            Assert.Equal(0m, item.MarkupPercent);
            Assert.Equal(0m, item.TotalCost);
        });

        // Pero en DB queda el costo REAL de la tarifa (el server sabe; el caller no lo ve).
        var storedItem = await context.QuoteItems.AsNoTracking()
            .SingleAsync(i => i.RateId == rate.Id);
        Assert.Equal(70m, storedItem.UnitCost);
        Assert.Equal(120m, storedItem.UnitPrice);
    }

    [Fact]
    public async Task AddItemAsync_FromRate_UserWithSeeCost_EchoesRealCost()
    {
        await using var context = CreateContext();
        var (quote, rate) = await SeedQuoteAndRateAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var detail = await service.AddItemAsync(
            quote.PublicId.ToString(),
            new UpsertQuoteItemRequest
            {
                ServiceType = "Excursion",
                Description = "lo completa la tarifa",
                Quantity = 1,
                UnitCost = 0m,
                UnitPrice = 0m,
                RateId = rate.PublicId.ToString()
            },
            CancellationToken.None);

        // Con permiso: el autocompletado se ve en el response, sin regresion.
        var added = detail.Items.Single(i => i.RatePublicId == rate.PublicId);
        Assert.Equal(70m, added.UnitCost);
        Assert.Equal(120m, added.UnitPrice);
    }
}
