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
/// Obra "informes por moneda" (2026-08-20): <c>GetSellerRankingAsync</c>, <c>GetDestinationAnalyticsAsync</c>
/// y <c>GetYearOverYearAsync</c> dejaron de ser <c>[Authorize(Roles="Admin")]</c> y pasaron a
/// <c>reportes.view</c> (ver <c>ReportsController</c>). Este archivo fija dos cosas a la vez:
/// <list type="bullet">
///   <item>P-3: cada linea de <c>...ByCurrency</c> es UNA moneda — dos monedas en juego nunca se suman
///         en un solo numero, ni en el desglose ni "sin querer" en el escalar legacy.</item>
///   <item>F-14: sin <c>cobranzas.see_cost</c>, costo y margen quedan afuera (escalar en 0, desglose por
///         moneda VACIO) — la venta se sigue viendo, no es informacion de costo.</item>
/// </list>
///
/// <para>Las fechas de seed usan <c>DateTime.UtcNow</c> a proposito (nunca una fecha fija): los 3
/// endpoints calculan su propia ventana por defecto en relacion a "ahora" (año actual / mes actual), asi
/// que sembrar "ahora" evita el bug de un solo dia que ya paso en esta obra (seed fijo + calculo relativo
/// a hoy).</para>
/// </summary>
public class ReportServiceBiAnalyticsCurrencyTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bi-analytics-currency-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>
    /// Atajo para los tests de MONEDA/ENMASCARADO (no de scope): siempre con <c>reservas.view_all</c>
    /// para que el <c>ownerFilter</c> nuevo (hallazgo de review 2026-08-20) quede afuera y no interfiera
    /// con lo que estos tests miden. El scope en si tiene su propia bateria mas abajo.
    /// </summary>
    private static ReportService BuildReportService(AppDbContext db, bool canSeeCost) =>
        BuildReportService(db, userId: "vendedor-test", canSeeCost: canSeeCost, hasViewAll: true);

    private static ReportService BuildReportService(AppDbContext db, string userId, bool canSeeCost, bool hasViewAll)
    {
        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((BnaUsdSellerRateDto?)null);

        // Rol SIEMPRE "Vendedor" (nunca "Admin"): estos tests ejercitan el camino de PERMISOS, no el
        // bypass de rol Admin (isAdmin=true saltea las dos preguntas de scope a la vez y taparia un bug
        // en cualquiera de las dos por separado).
        var accessor = BuildHttpContextAccessor(userId, "Vendedor");

        var permissions = new List<string>();
        if (canSeeCost) permissions.Add(Permissions.CobranzasSeeCost);
        if (hasViewAll) permissions.Add(Permissions.ReservasViewAll);
        var resolver = BuildResolver(userId, permissions.ToArray());

        return new ReportService(db, bna.Object, resolver, accessor);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, string role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId), new(ClaimTypes.Role, role) };
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    // ================================================================================
    // GetSellerRankingAsync
    // ================================================================================

    private static async Task SeedSellerWithTwoCurrenciesAsync(AppDbContext db)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "R-BI-0001",
            Name = "Reserva bimoneda",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "seller-A",
            ResponsibleUserName = "Vendedor A",
            ConfirmedSale = 1000m, // escalar legacy: los 700 ARS + 300 USD "sumados" tal cual estaban antes
            TotalCost = 600m,
            CreatedAt = DateTime.UtcNow,
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        db.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.ARS, ConfirmedSale = 700m, TotalCost = 400m, TotalSale = 700m },
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.USD, ConfirmedSale = 300m, TotalCost = 200m, TotalSale = 300m });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SellerRanking_TwoCurrencies_NeverMixedInOneNumber()
    {
        await using var db = NewDbContext();
        await SeedSellerWithTwoCurrenciesAsync(db);

        var ranking = await BuildReportService(db, canSeeCost: true).GetSellerRankingAsync(null, null, CancellationToken.None);

        var row = Assert.Single(ranking);
        Assert.Equal(2, row.TotalSalesByCurrency.Count);
        Assert.Equal(700m, row.TotalSalesByCurrency.Single(c => c.Currency == Monedas.ARS).Amount);
        Assert.Equal(300m, row.TotalSalesByCurrency.Single(c => c.Currency == Monedas.USD).Amount);
        Assert.Equal(2, row.TotalCostsByCurrency.Count);
        Assert.Equal(400m, row.TotalCostsByCurrency.Single(c => c.Currency == Monedas.ARS).Amount);
        Assert.Equal(200m, row.TotalCostsByCurrency.Single(c => c.Currency == Monedas.USD).Amount);
    }

    [Fact]
    public async Task SellerRanking_WithoutSeeCost_MasksCostAndMargin_ButKeepsSalesVisible()
    {
        await using var db = NewDbContext();
        await SeedSellerWithTwoCurrenciesAsync(db);

        var ranking = await BuildReportService(db, canSeeCost: false).GetSellerRankingAsync(null, null, CancellationToken.None);

        var row = Assert.Single(ranking);
        // Venta: se sigue viendo, escalar y por moneda.
        Assert.Equal(1000m, row.TotalSales);
        Assert.Equal(2, row.TotalSalesByCurrency.Count);
        // Costo/margen: escalar en 0, desglose por moneda VACIO (no en 0 — afuera).
        Assert.Equal(0m, row.TotalCosts);
        Assert.Equal(0m, row.GrossMargin);
        Assert.Equal(0m, row.MarginPercent);
        Assert.Empty(row.TotalCostsByCurrency);
        Assert.Empty(row.GrossMarginByCurrency);
    }

    // ================================================================================
    // GetDestinationAnalyticsAsync
    // ================================================================================

    private static async Task SeedDestinationWithTwoCurrenciesAsync(AppDbContext db)
    {
        // GetDestinationAnalyticsAsync ahora hace INNER JOIN contra Reservas (hallazgo de review
        // 2026-08-20, para poder recortar por scope.OwnerFilter): sin esta fila padre, el hotel quedaria
        // afuera del resultado aunque el filtro de owner este desactivado.
        db.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "R-BI-DEST-1", Name = "Reserva con hotel bimoneda",
            Status = EstadoReserva.Confirmed, ResponsibleUserId = "vendedor-test", CreatedAt = DateTime.UtcNow,
        });
        db.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, City = "Bariloche", Currency = Monedas.ARS,
            SalePrice = 1000m, NetCost = 600m, Adults = 2, Children = 0, CreatedAt = DateTime.UtcNow,
        });
        db.HotelBookings.Add(new HotelBooking
        {
            Id = 2, ReservaId = 1, City = "Bariloche", Currency = Monedas.USD,
            SalePrice = 500m, NetCost = 300m, Adults = 2, Children = 0, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Destinations_TwoCurrencies_NeverMixedInOneNumber()
    {
        await using var db = NewDbContext();
        await SeedDestinationWithTwoCurrenciesAsync(db);

        var destinations = await BuildReportService(db, canSeeCost: true).GetDestinationAnalyticsAsync(null, null, CancellationToken.None);

        var row = Assert.Single(destinations);
        Assert.Equal("BARILOCHE", row.Destination);
        Assert.Equal(2, row.TotalRevenueByCurrency.Count);
        Assert.Equal(1000m, row.TotalRevenueByCurrency.Single(c => c.Currency == Monedas.ARS).Amount);
        Assert.Equal(500m, row.TotalRevenueByCurrency.Single(c => c.Currency == Monedas.USD).Amount);
        Assert.Equal(600m, row.TotalCostByCurrency.Single(c => c.Currency == Monedas.ARS).Amount);
        Assert.Equal(300m, row.TotalCostByCurrency.Single(c => c.Currency == Monedas.USD).Amount);
    }

    [Fact]
    public async Task Destinations_WithoutSeeCost_MasksCostAndMargin_ButKeepsRevenueVisible()
    {
        await using var db = NewDbContext();
        await SeedDestinationWithTwoCurrenciesAsync(db);

        var destinations = await BuildReportService(db, canSeeCost: false).GetDestinationAnalyticsAsync(null, null, CancellationToken.None);

        var row = Assert.Single(destinations);
        Assert.Equal(1500m, row.TotalRevenue);
        Assert.Equal(2, row.TotalRevenueByCurrency.Count);
        Assert.Equal(0m, row.TotalCost);
        Assert.Equal(0m, row.Margin);
        Assert.Empty(row.TotalCostByCurrency);
        Assert.Empty(row.MarginByCurrency);
    }

    // ================================================================================
    // GetYearOverYearAsync
    // ================================================================================

    private static async Task SeedYoyWithTwoCurrenciesInCurrentMonthAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        db.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "R-BI-YOY-1", Name = "Reserva ARS", Status = EstadoReserva.Confirmed,
            TotalSale = 1000m, TotalCost = 600m, CreatedAt = now,
        });
        db.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "R-BI-YOY-2", Name = "Reserva USD", Status = EstadoReserva.Confirmed,
            TotalSale = 200m, TotalCost = 100m, CreatedAt = now,
        });
        await db.SaveChangesAsync();

        db.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = Monedas.ARS, TotalSale = 1000m, TotalCost = 600m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = Monedas.USD, TotalSale = 200m, TotalCost = 100m });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Yoy_TwoCurrenciesInSameMonth_NeverMixedInOneNumber()
    {
        await using var db = NewDbContext();
        await SeedYoyWithTwoCurrenciesInCurrentMonthAsync(db);

        var response = await BuildReportService(db, canSeeCost: true).GetYearOverYearAsync(CancellationToken.None);

        var currentMonth = response.CurrentYear.Single(m => m.MonthNumber == DateTime.UtcNow.Month);
        Assert.Equal(2, currentMonth.SalesByCurrency.Count);
        Assert.Equal(1000m, currentMonth.SalesByCurrency.Single(c => c.Currency == Monedas.ARS).Amount);
        Assert.Equal(200m, currentMonth.SalesByCurrency.Single(c => c.Currency == Monedas.USD).Amount);
        Assert.Equal(600m, currentMonth.CostsByCurrency.Single(c => c.Currency == Monedas.ARS).Amount);
        Assert.Equal(100m, currentMonth.CostsByCurrency.Single(c => c.Currency == Monedas.USD).Amount);
    }

    [Fact]
    public async Task Yoy_WithoutSeeCost_MasksCostAndMargin_ButKeepsSalesVisible()
    {
        await using var db = NewDbContext();
        await SeedYoyWithTwoCurrenciesInCurrentMonthAsync(db);

        var response = await BuildReportService(db, canSeeCost: false).GetYearOverYearAsync(CancellationToken.None);

        var currentMonth = response.CurrentYear.Single(m => m.MonthNumber == DateTime.UtcNow.Month);
        Assert.Equal(1200m, currentMonth.Sales);
        Assert.Equal(2, currentMonth.SalesByCurrency.Count);
        Assert.Equal(0m, currentMonth.Costs);
        Assert.Equal(0m, currentMonth.Margin);
        Assert.Empty(currentMonth.CostsByCurrency);
        Assert.Empty(currentMonth.MarginByCurrency);
    }

    // ================================================================================
    // scope.OwnerFilter (hallazgo de review 2026-08-20, bloqueante backend+security): el criterio firmado
    // en GetDashboardAsync ("el vendedor no ve los numeros de toda la agencia SIN EXCEPCIONES") tambien
    // rige estos 3 endpoints — sin reservas.view_all, un vendedor NO puede comparar su venta contra la de
    // sus companeros. "vendedor-test" es el que consulta; "otro-vendedor" es dueño de la reserva ajena.
    // ================================================================================

    private static async Task SeedTwoSellersWithOwnAndForeignReservaAsync(AppDbContext db)
    {
        db.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "R-BI-SCOPE-1", Name = "Reserva propia",
            Status = EstadoReserva.Confirmed, ResponsibleUserId = "vendedor-test", ResponsibleUserName = "Vendedor Test",
            ConfirmedSale = 1000m, TotalSale = 1000m, TotalCost = 600m, CreatedAt = DateTime.UtcNow,
        });
        db.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "R-BI-SCOPE-2", Name = "Reserva ajena",
            Status = EstadoReserva.Confirmed, ResponsibleUserId = "otro-vendedor", ResponsibleUserName = "Otro Vendedor",
            ConfirmedSale = 5000m, TotalSale = 5000m, TotalCost = 3000m, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        db.HotelBookings.AddRange(
            new HotelBooking { ReservaId = 1, City = "Propio", Currency = Monedas.ARS, SalePrice = 1000m, NetCost = 600m, Adults = 2, CreatedAt = DateTime.UtcNow },
            new HotelBooking { ReservaId = 2, City = "Ajeno", Currency = Monedas.ARS, SalePrice = 5000m, NetCost = 3000m, Adults = 2, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SellerRanking_WithoutViewAll_SeesOnlyOwnRow()
    {
        await using var db = NewDbContext();
        await SeedTwoSellersWithOwnAndForeignReservaAsync(db);

        var service = BuildReportService(db, userId: "vendedor-test", canSeeCost: true, hasViewAll: false);
        var ranking = await service.GetSellerRankingAsync(null, null, CancellationToken.None);

        var row = Assert.Single(ranking);
        Assert.Equal("vendedor-test", row.UserId);
        Assert.Equal(1000m, row.TotalSales);
    }

    [Fact]
    public async Task SellerRanking_WithViewAll_SeesWholeAgency()
    {
        await using var db = NewDbContext();
        await SeedTwoSellersWithOwnAndForeignReservaAsync(db);

        var service = BuildReportService(db, userId: "vendedor-test", canSeeCost: true, hasViewAll: true);
        var ranking = await service.GetSellerRankingAsync(null, null, CancellationToken.None);

        Assert.Equal(2, ranking.Count);
        Assert.Contains(ranking, r => r.UserId == "vendedor-test");
        Assert.Contains(ranking, r => r.UserId == "otro-vendedor");
    }

    [Fact]
    public async Task Destinations_WithoutViewAll_SeesOnlyOwnPortfolio()
    {
        await using var db = NewDbContext();
        await SeedTwoSellersWithOwnAndForeignReservaAsync(db);

        var service = BuildReportService(db, userId: "vendedor-test", canSeeCost: true, hasViewAll: false);
        var destinations = await service.GetDestinationAnalyticsAsync(null, null, CancellationToken.None);

        var row = Assert.Single(destinations);
        Assert.Equal("PROPIO", row.Destination);
    }

    [Fact]
    public async Task Destinations_WithViewAll_SeesWholeAgency()
    {
        await using var db = NewDbContext();
        await SeedTwoSellersWithOwnAndForeignReservaAsync(db);

        var service = BuildReportService(db, userId: "vendedor-test", canSeeCost: true, hasViewAll: true);
        var destinations = await service.GetDestinationAnalyticsAsync(null, null, CancellationToken.None);

        Assert.Equal(2, destinations.Count);
        Assert.Contains(destinations, d => d.Destination == "PROPIO");
        Assert.Contains(destinations, d => d.Destination == "AJENO");
    }

    [Fact]
    public async Task Yoy_WithoutViewAll_SeesOnlyOwnPortfolio()
    {
        await using var db = NewDbContext();
        await SeedTwoSellersWithOwnAndForeignReservaAsync(db);

        var service = BuildReportService(db, userId: "vendedor-test", canSeeCost: true, hasViewAll: false);
        var response = await service.GetYearOverYearAsync(CancellationToken.None);

        var currentMonth = response.CurrentYear.Single(m => m.MonthNumber == DateTime.UtcNow.Month);
        Assert.Equal(1000m, currentMonth.Sales);
        Assert.Equal(1, currentMonth.ReservaCount);
    }

    [Fact]
    public async Task Yoy_WithViewAll_SeesWholeAgency()
    {
        await using var db = NewDbContext();
        await SeedTwoSellersWithOwnAndForeignReservaAsync(db);

        var service = BuildReportService(db, userId: "vendedor-test", canSeeCost: true, hasViewAll: true);
        var response = await service.GetYearOverYearAsync(CancellationToken.None);

        var currentMonth = response.CurrentYear.Single(m => m.MonthNumber == DateTime.UtcNow.Month);
        Assert.Equal(6000m, currentMonth.Sales);
        Assert.Equal(2, currentMonth.ReservaCount);
    }
}
